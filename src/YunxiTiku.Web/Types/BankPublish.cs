using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using Puller = JkdWeb.BankPuller;
using static AppHelpers;

/// <summary>
/// 题库发布的「版本目录 + 原子切换」（BE-07）。
///
/// 旧实现是"先把上传包里的库覆盖到线上、再删光 live assets 整棵复制"：
/// 复制中途失败/磁盘满/关机 → 新库配缺图（或旧库配新图）；健康检查失败也只回拷 DB、
/// 不恢复 assets。单课导入更是把**源包整棵 assets** 盖到线上（连别的课的图一起动），
/// 任一张图失败也不撤销已经提交的库事务。
///
/// 现在：
///   · DB 与 assets 先在**同一文件系统**的版本目录里准备好、校验通过（线上文件一个字节不动）；
///   · 切换只用同卷 rename（DB 一个文件、assets 一棵目录），任何一步失败按逆序撤销；
///   · 切换日志（data/_publish/*.json）留在盘上，进程中途死掉由
///     <see cref="RecoverPending"/> 在下次启动时收尾 —— 线上要么整套旧的、要么整套新的。
/// </summary>
internal static class BankPublish
{
    /// <summary>发布用的路径（绝对路径，且都在同卷）。</summary>
    internal sealed class PublishJournal
    {
        public string Kind { get; set; } = "";            // bank | course
        public string State { get; set; } = "prepared";   // prepared | committed
        public string Name { get; set; } = "";
        public string LiveDb { get; set; } = "";
        public string LiveAssets { get; set; } = "";
        public string OldDb { get; set; } = "";
        public string OldAssets { get; set; } = "";
        public string StagedDb { get; set; } = "";
        public string StagedAssets { get; set; } = "";
        public int CourseId { get; set; }
        public string StampName { get; set; } = "";
        public int StampChapters { get; set; }
        public int StampSubjects { get; set; }
        public string CreatedAt { get; set; } = "";
    }

    public static string DataRoot(AppPaths paths)
        => Directory.GetParent(Path.GetFullPath(paths.SqlitePath))?.FullName ?? paths.BaseRoot;

    public static string VersionsRoot(AppPaths paths) => Path.Combine(DataRoot(paths), "_versions");

    public static string JournalsRoot(AppPaths paths) => Path.Combine(DataRoot(paths), "_publish");

    /// <summary>开一个干净的版本目录（与线上同一文件系统，切换才能用 rename）。</summary>
    public static string NewStagingDir(AppPaths paths, string name)
    {
        var dir = Path.Combine(VersionsRoot(paths), name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- 整包发布 ---------------------------------------------------------

    /// <summary>
    /// 把线上 DB + assets 换成版本目录里的那一套。健康检查不通过 → 整套撤销后抛。
    /// 调用方保证 <paramref name="stagedDb"/> 已经过校验、且已把本地改动重贴进去。
    /// </summary>
    public static void PublishBank(AppPaths paths, string name, string stagedDb, string stagedAssets)
    {
        // 版本目录里那份库刚被写过（重贴本地改动），把空闲池连接关掉让 SQLite 自己把 WAL
        // checkpoint 回主文件 —— 这样只搬 .db 也不会漏掉刚写进去的内容（见 SwapIn 的说明）。
        SqliteConnection.ClearAllPools();
        var journal = new PublishJournal
        {
            Kind = "bank",
            Name = name,
            LiveDb = Path.GetFullPath(paths.SqlitePath),
            LiveAssets = Path.GetFullPath(paths.DataAssetsRoot),
            OldDb = Path.GetFullPath(paths.SqlitePath) + ".old-" + name,
            OldAssets = Path.GetFullPath(paths.DataAssetsRoot).TrimEnd(Path.DirectorySeparatorChar) + ".old-" + name,
            StagedDb = Path.GetFullPath(stagedDb),
            StagedAssets = Path.GetFullPath(stagedAssets),
            CreatedAt = NowText(),
        };
        var journalPath = WriteJournal(paths, journal);
        try
        {
            SwapIn(journal, journalPath);
            if (!QuestionBank.ProbeHealthy(journal.LiveDb))
            {
                throw new InvalidOperationException("上传的题库无法正常读取（缺少必要的表/列），已自动回滚到原题库");
            }
            journal.State = "committed";
            WriteJsonAtomic(journalPath, journal);
            CleanupOld(journal);
            TryDeleteFile(journalPath);
        }
        catch
        {
            // 撤销：DB 与 assets 一起回上一版本（旧实现在这条分支上只回拷 DB，图片留在半套上）
            Undo(journal, journalPath);
            throw;
        }
    }

    // ---- 单课 assets 发布（带日志，与库合并建立关联） ----------------------

    /// <summary>
    /// 单课导入的 assets 切换：只动 <c>assets/&lt;courseId&gt;</c>，旧的一份先挪到版本目录里，
    /// 等库那一步确认落库后再删。返回发布对象，调用方在库操作成功后调用 <see cref="CourseAssetPublish.Commit"/>。
    /// </summary>
    public static CourseAssetPublish PublishCourseAssets(AppPaths paths, string name, int courseId, string stagedCourseAssets)
    {
        var liveAssets = Path.GetFullPath(Path.Combine(paths.DataAssetsRoot, courseId.ToString(CultureInfo.InvariantCulture)));
        var journal = new PublishJournal
        {
            Kind = "course",
            Name = name,
            CourseId = courseId,
            LiveDb = Path.GetFullPath(paths.SqlitePath),
            LiveAssets = liveAssets,
            OldAssets = Path.Combine(VersionsRoot(paths), name, "old", courseId.ToString(CultureInfo.InvariantCulture)),
            StagedAssets = Path.GetFullPath(stagedCourseAssets),
            CreatedAt = NowText(),
        };
        var journalPath = WriteJournal(paths, journal);
        var publish = new CourseAssetPublish(journal, journalPath);
        publish.SwapIn();
        return publish;
    }

    /// <summary>单课 assets 的一次发布：切换 / 提交 / 撤销。</summary>
    internal sealed class CourseAssetPublish
    {
        private readonly PublishJournal _journal;
        private readonly string _journalPath;

        internal CourseAssetPublish(PublishJournal journal, string journalPath)
        {
            _journal = journal;
            _journalPath = journalPath;
        }

        public string JournalPath => _journalPath;

        /// <summary>把这份导入的"课程指纹"写进日志：崩溃后据此判断库那一步到底落库没有。</summary>
        public void StampCourse(string name, int chapters, int subjects)
        {
            _journal.StampName = name;
            _journal.StampChapters = chapters;
            _journal.StampSubjects = subjects;
            WriteJsonAtomic(_journalPath, _journal);
        }

        internal void SwapIn()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_journal.OldAssets) ?? ".");
                if (Directory.Exists(_journal.LiveAssets))
                {
                    TryDeleteDirectory(_journal.OldAssets);
                    Directory.Move(_journal.LiveAssets, _journal.OldAssets);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_journal.LiveAssets) ?? ".");
                Directory.Move(_journal.StagedAssets, _journal.LiveAssets);
                _journal.State = "assets-published";
                WriteJsonAtomic(_journalPath, _journal);
            }
            catch
            {
                // 只撤 assets：库那一步还没做，回到旧目录即可
                UndoCourseAssets(_journal);
                throw;
            }
        }

        /// <summary>库那一步已经成功 → 删掉旧版本、结束这次发布。</summary>
        public void Commit()
        {
            _journal.State = "committed";
            try { WriteJsonAtomic(_journalPath, _journal); } catch (Exception ex) { Console.WriteLine($"[publish] 写发布日志失败（不影响本次导入）: {ex.Message}"); }
            TryDeleteDirectory(_journal.OldAssets);
            TryDeleteDirectory(_journal.StagedAssets);
            TryDeleteFile(_journalPath);
        }

        /// <summary>库那一步失败/回滚 → assets 也回到上一版本。</summary>
        public void Rollback()
        {
            UndoCourseAssets(_journal);
            TryDeleteFile(_journalPath);
        }
    }

    private static void UndoCourseAssets(PublishJournal journal)
    {
        try
        {
            if (Directory.Exists(journal.OldAssets))
            {
                TryDeleteDirectory(journal.LiveAssets);
                Directory.CreateDirectory(Path.GetDirectoryName(journal.LiveAssets) ?? ".");
                Directory.Move(journal.OldAssets, journal.LiveAssets);
            }
            TryDeleteDirectory(journal.StagedAssets);
        }
        catch (Exception ex)
        {
            // 撤销本身失败：日志文件**不删**，留给下次启动的 RecoverPending 继续收尾
            Console.WriteLine($"[publish-rollback-failed] course={journal.CourseId} {ex.Message}");
            throw new InvalidOperationException($"题库资源回滚失败，请重启服务或从备份 {journal.OldAssets} 恢复：{ex.Message}", ex);
        }
    }

    // ---- 崩溃恢复 ---------------------------------------------------------

    /// <summary>
    /// 启动时收尾上次没做完的发布（data/_publish/*.json）：
    /// 让线上的 DB 与 assets 回到"同一版本"，绝不留下半套。
    /// </summary>
    public static void RecoverPending(AppPaths paths)
    {
        var root = JournalsRoot(paths);
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            PublishJournal? journal;
            try
            {
                journal = JsonSerializer.Deserialize<PublishJournal>(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[publish-recover] 发布日志 {Path.GetFileName(file)} 读不了（保留，人工处理）: {ex.Message}");
                continue;
            }
            if (journal is null || journal.Kind is not ("bank" or "course"))
            {
                Console.WriteLine($"[publish-recover] 发布日志 {Path.GetFileName(file)} 无法识别（保留，人工处理）");
                continue;
            }
            try
            {
                if (journal.Kind == "bank") RecoverBank(journal, file);
                else RecoverCourse(paths, journal, file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[publish-recover] {Path.GetFileName(file)} 收尾失败（保留，人工处理）: {ex.Message}");
            }
        }
        PruneLeftovers(paths);
    }

    /// <summary>
    /// 清理陈旧的版本目录与 `.old-*` 残留（一天前的）。只删没人引用的：
    /// 正在用的版本在发布结束时就删了，这里兜的是"删除失败/进程被杀"留下的垃圾。
    /// </summary>
    private static void PruneLeftovers(AppPaths paths)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            var versions = VersionsRoot(paths);
            if (Directory.Exists(versions))
            {
                foreach (var dir in Directory.EnumerateDirectories(versions))
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff) TryDeleteDirectory(dir);
                }
            }
            var dataRoot = DataRoot(paths);
            foreach (var entry in Directory.EnumerateFileSystemEntries(dataRoot, "*.old-*", SearchOption.TopDirectoryOnly))
            {
                var updated = File.GetLastWriteTimeUtc(entry);
                if (updated < cutoff)
                {
                    if (Directory.Exists(entry)) TryDeleteDirectory(entry);
                    else TryDeleteFile(entry);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[publish-prune] 清理旧版本失败（忽略）: {ex.Message}");
        }
    }

    private static void RecoverBank(PublishJournal journal, string journalPath)
    {
        var dbSwapped = !File.Exists(journal.StagedDb) && File.Exists(journal.LiveDb);
        var assetsSwapped = !Directory.Exists(journal.StagedAssets) && Directory.Exists(journal.LiveAssets);
        if (assetsSwapped)
        {
            Console.WriteLine($"[publish-recover] 整包发布已完成，清理旧版本");
            CleanupOld(journal);
            TryDeleteFile(journalPath);
            return;
        }
        if (dbSwapped)
        {
            // 只换了一半（DB 新、assets 旧）→ 把 assets 也换过去；不健康就整个退回去
            Console.WriteLine($"[publish-recover] 上次整包发布中断在 assets 切换之前，继续切换");
            try
            {
                if (Directory.Exists(journal.LiveAssets))
                {
                    TryDeleteDirectory(journal.OldAssets);
                    Directory.Move(journal.LiveAssets, journal.OldAssets);
                }
                Directory.Move(journal.StagedAssets, journal.LiveAssets);
                if (!QuestionBank.ProbeHealthy(journal.LiveDb))
                {
                    throw new InvalidOperationException("回收后的题库读不通");
                }
                CleanupOld(journal);
                TryDeleteFile(journalPath);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[publish-recover] 继续切换失败，改回滚到上一版本: {ex.Message}");
                Undo(journal, journalPath);
                return;
            }
        }
        Console.WriteLine($"[publish-recover] 上次整包发布还没开始切换，清理版本目录");
        CleanupStaged(journal);
        TryDeleteFile(journalPath);
    }

    private static void RecoverCourse(AppPaths paths, PublishJournal journal, string journalPath)
    {
        var landed = CourseMatchesStamp(journal);
        if (journal.State == "committed" || landed)
        {
            Console.WriteLine($"[publish-recover] 课程 {journal.CourseId} 的导入已落库，补做/清理 assets 切换");
            if (!Directory.Exists(journal.LiveAssets) && Directory.Exists(journal.StagedAssets))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(journal.LiveAssets) ?? ".");
                Directory.Move(journal.StagedAssets, journal.LiveAssets);
            }
            TryDeleteDirectory(journal.OldAssets);
            TryDeleteDirectory(journal.StagedAssets);
            TryDeleteFile(journalPath);
            return;
        }
        Console.WriteLine($"[publish-recover] 课程 {journal.CourseId} 的导入没有落库，把 assets 退回上一版本");
        if (Directory.Exists(journal.OldAssets))
        {
            TryDeleteDirectory(journal.LiveAssets);
            Directory.CreateDirectory(Path.GetDirectoryName(journal.LiveAssets) ?? ".");
            Directory.Move(journal.OldAssets, journal.LiveAssets);
        }
        TryDeleteDirectory(journal.StagedAssets);
        TryDeleteFile(journalPath);
    }

    /// <summary>库里的这门课是不是就是这份包（导入已落库）：按课程名 + 章节/题目数比对。</summary>
    private static bool CourseMatchesStamp(PublishJournal journal)
    {
        if (!File.Exists(journal.LiveDb) || journal.StampName.Length == 0) return false;
        try
        {
            var info = AdminDataTransfer.ReadCourseImportInfo(journal.LiveDb, journal.CourseId);
            return info.Name == journal.StampName
                   && info.Chapters == journal.StampChapters
                   && info.Subjects == journal.StampSubjects;
        }
        catch
        {
            return false;
        }
    }

    // ---- 内部 -------------------------------------------------------------

    private static void SwapIn(PublishJournal journal, string journalPath)
    {
        /* ⚠️ SQLite 的 -wal / -shm 必须跟着库文件一起换，否则新库旁边留着一份**旧库的 WAL**：
           下一次打开会把旧 WAL 里的页重放到新库上（或在最坏情况下报 malformed）。
           顺序：先把旧库的 sidecar 挪开（挪不动就中止 —— 说明还有活动连接占着，此时一个字节都还没动），
           再换库文件，最后把版本目录里那份库自己的 sidecar 一起搬过来。
           调用方已经 ClearAllPools 过一次（空闲池连接关闭时 SQLite 会把 WAL checkpoint 掉），
           所以这几步通常都是空操作。 */
        var movedSidecars = 0;
        foreach (var suffix in SqliteSidecarSuffixes)
        {
            var live = journal.LiveDb + suffix;
            if (!File.Exists(live)) continue;
            var stash = live + ".old-" + journal.Name;
            TryDeleteFile(stash);
            File.Move(live, stash);
            movedSidecars++;
        }
        if (movedSidecars > 0) Console.WriteLine($"[publish] 换库前把 {movedSidecars} 个 SQLite 附属文件（-wal/-shm/-journal）挪到一边");

        // 1) DB：线上 → 旧位置；版本目录 → 线上
        if (File.Exists(journal.LiveDb))
        {
            TryDeleteFile(journal.OldDb);
            File.Move(journal.LiveDb, journal.OldDb);
        }
        File.Move(journal.StagedDb, journal.LiveDb);
        // 版本目录里那份库可能自己带 sidecar（重贴本地改动是在版本目录里写的，WAL 模式）
        foreach (var suffix in SqliteSidecarSuffixes)
        {
            var staged = journal.StagedDb + suffix;
            if (!File.Exists(staged)) continue;
            TryDeleteFile(journal.LiveDb + suffix);
            File.Move(staged, journal.LiveDb + suffix);
        }
        // 旧库的 sidecar 已经在 Undo/CleanupOld 里处理，这里先留档（stash 路径由 live+suffix 推出）
        // 2) assets 同理（目录 rename）
        if (!Directory.Exists(journal.StagedAssets)) Directory.CreateDirectory(journal.StagedAssets);
        if (Directory.Exists(journal.LiveAssets))
        {
            TryDeleteDirectory(journal.OldAssets);
            Directory.Move(journal.LiveAssets, journal.OldAssets);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(journal.LiveAssets) ?? ".");
        Directory.Move(journal.StagedAssets, journal.LiveAssets);
    }

    /// <summary>库文件旁边要一起管理/清理的 SQLite 附属文件。</summary>
    private static readonly string[] SqliteSidecarSuffixes = { "-wal", "-shm", "-journal" };

    private static void Undo(PublishJournal journal, string journalPath)
    {
        // 逆序：assets 先回（只有"旧的一份已经挪走"时才需要回），再回 DB
        try
        {
            if (Directory.Exists(journal.OldAssets))
            {
                TryDeleteDirectory(journal.LiveAssets);
                Directory.CreateDirectory(Path.GetDirectoryName(journal.LiveAssets) ?? ".");
                Directory.Move(journal.OldAssets, journal.LiveAssets);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[publish-undo-failed] assets 回滚失败: {ex.Message}");
        }
        try
        {
            if (File.Exists(journal.OldDb))
            {
                TryDeleteFile(journal.LiveDb);
                File.Move(journal.OldDb, journal.LiveDb);
            }
            // 旧库的 -wal/-shm：把新库带来的那份删掉，再把旧那份挪回来（顺序不能反）
            foreach (var suffix in SqliteSidecarSuffixes)
            {
                var live = journal.LiveDb + suffix;
                var stash = live + ".old-" + journal.Name;
                if (!File.Exists(stash)) continue;
                TryDeleteFile(live);
                File.Move(stash, live);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[publish-undo-failed] DB 回滚失败: {ex.Message}");
        }
        CleanupStaged(journal);
        TryDeleteFile(journalPath);
        SqliteConnection.ClearAllPools();
    }

    private static void CleanupOld(PublishJournal journal)
    {
        TryDeleteFile(journal.OldDb);
        TryDeleteDirectory(journal.OldAssets);
        foreach (var suffix in SqliteSidecarSuffixes)
        {
            TryDeleteFile(journal.LiveDb + suffix + ".old-" + journal.Name);
            TryDeleteFile(journal.OldDb + suffix);
        }
    }

    private static void CleanupStaged(PublishJournal journal)
    {
        TryDeleteFile(journal.StagedDb);
        TryDeleteDirectory(journal.StagedAssets);
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static string WriteJournal(AppPaths paths, PublishJournal journal)
    {
        var root = JournalsRoot(paths);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, journal.Kind + "-" + journal.Name + ".json");
        WriteJsonAtomic(path, journal);
        return path;
    }
}
