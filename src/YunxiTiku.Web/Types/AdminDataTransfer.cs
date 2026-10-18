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

static class AdminDataTransfer
{
    /// <summary>
    /// 两个备份根的条目清单，新的在前。
    ///
    /// 形状差异很大，所以各自归类后再并成一条流：
    ///   - `userdata/_backups/*.zip`  —— 用户数据整包（ImportUserData 每次导入前自动留）
    ///   - `data/_backups/<目录>`     —— 题库快照（换库、删题、拉课都会留一个目录）
    /// 目录类备份只有名字能看出做了什么，所以把名字里的类型前缀拆出来当 kind 显示。
    /// </summary>
    public static List<object> ListBackups(AppPaths paths)
    {
        var items = new List<(DateTime Updated, object Item)>();

        void Collect(string root, string kind, bool isDirectory)
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                if (isDirectory != info is DirectoryInfo) continue;   // 只取自己那一种
                var name = info.Name;
                // Uploads 等临时目录不算备份
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                items.Add((info.LastWriteTime, new
                {
                    name,
                    kind,
                    isDirectory,
                    updatedAt = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    size = isDirectory ? DirectorySize(path) : ((FileInfo)info).Length,
                    // 能否回滚：zip 式的用户数据可以；题库目录要认得结构才行，交给 restore 端点判
                    restorable = kind is "userdata" or "bank"
                }));
            }
        }

        Collect(Path.Combine(paths.UserDataRoot, "_backups"), "userdata", false);
        var bankRoot = Path.Combine(Directory.GetParent(paths.SqlitePath)?.FullName ?? paths.BaseRoot, "_backups");
        Collect(bankRoot, "bank", true);

        return items.OrderByDescending(entry => entry.Updated).Select(entry => entry.Item).ToList();
    }

    /// <summary>
    /// 把前端传来的备份名解析成真实文件路径 —— **必须**拒绝一切路径分隔符。
    /// 否则 `name=../../etc/passwd` 就能把服务器上的任意文件读下来。
    /// 只允许 `_backups` 根目录下的一层名字，且必须是之前列出来的那种。
    /// </summary>
    public static (string Path, string DownloadName)? ResolveBackup(AppPaths paths, string name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) return null;
        if (clean.Contains('/') || clean.Contains('\\') || clean.Contains("..")) return null;
        if (clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        var userZip = Path.Combine(paths.UserDataRoot, "_backups", clean);
        if (clean.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(userZip))
            return (userZip, clean);

        var bankRoot = Path.Combine(Directory.GetParent(paths.SqlitePath)?.FullName ?? paths.BaseRoot, "_backups");
        var bankDir = Path.Combine(bankRoot, clean);
        if (Directory.Exists(bankDir)) return (bankDir, clean);

        return null;
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 从一个已有备份回滚。两类备份走不同通道，但**都必须先给当前状态再留一份**——
    /// 用户点回滚多半是刚发现数据不对，这时候如果回滚本身再出错就是二次事故。
    ///
    ///   userdata/*.zip  → 拷贝到临时 .zip 后丢给 ImportUserData（它会自己再备份当前状态，
    ///                     并把「覆盖/删除现有文件」做成两阶段可撤销）
    ///   bank/&lt;目录&gt;    → 目录里就是 question-bank.db + assets，交给 PublishBank，
    ///                     健康检查不过会自动撤回到原题库
    /// </summary>
    public static IResult RestoreBackup(AppPaths paths, AuthStore auth, UserDataStore userData, string name)
    {
        var resolved = ResolveBackup(paths, name);
        if (resolved is null) return Results.Json(new { ok = false, error = "备份不存在或已过保留期清理" }, statusCode: 404);
        var (source, downloadName) = resolved.Value;

        if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var temp = Path.Combine(Path.GetTempPath(), $"yunxi-restore-{Guid.NewGuid():N}.zip");
            try
            {
                File.Copy(source, temp, true);
                var result = ImportUserData(paths, auth, userData, temp, downloadName);
                return Results.Json(result);
            }
            finally
            {
                TryDeleteFile(temp);
            }
        }

        if (!Directory.Exists(source)) return Results.Json(new { ok = false, error = "备份不存在" }, statusCode: 404);
        var stagedDb = FindQuestionDb(source)
                       ?? throw new InvalidOperationException("这个备份目录里没有 question-bank.db，无法回滚题库");
        // 备份目录里的 assets 可能直接就是 assets/，也可能和 db 同级，统一按 FindAssetsDirectory 找
        var stagedAssets = FindAssetsDirectory(source, stagedDb);
        if (stagedAssets is null || !Directory.Exists(stagedAssets))
            throw new InvalidOperationException("这个备份目录里没有 assets，无法回滚题库（图片会全丢）");

        var publishName = "restore-" + TimestampUnique();
        BankPublish.PublishBank(paths, publishName, stagedDb, stagedAssets);
        return Results.Json(new
        {
            ok = true,
            type = "bank",
            message = "题库已回滚到该备份",
            restored = name,
            status = GetStatus(paths)
        });
    }

    /// <summary>
    /// 导入之后的广告清洗 —— 整包上传（<see cref="ImportBank"/>）与单课导入
    /// （<see cref="ImportCourseBank"/>）都走这里。
    ///
    /// 以前只有「拉取」（PullEngine 的 CleanAds）和 /api/admin/clean-ads 会清广告，
    /// 上传/导入进来的库里带的促销句原样进库、直接展示给学员。现在导入路径也清。
    ///
    /// 为什么**同步**清、不给开关：导入本身就是重活（解包 + 整库替换 + assets 拷贝），
    /// 再跑一遍全库正则的代价实测 ~0.2-0.5s/3-5.5 万题（AdCleaner.CleanDatabase 一遍扫全表），
    /// 比"上传完还要管理员再点一次清广告"省事，也堵住"导入到手动清洗之间"那段展示窗口；
    /// 管理员事后仍可用 /api/admin/clean-ads 手动清（那条路径带独立备份）。
    ///
    /// 失败**不**让导入失败：导入本身已经成功、备份也在，广告最多是晚一点清掉，
    /// 所以只记日志并把 ads 返回 null。
    /// </summary>
    private static object? CleanAdsAfterImport(string dbPath, int courseId)
    {
        try
        {
            // 备份在导入流程里已经做过（整库 + assets），这里不再复制一份
            var result = Puller.AdCleaner.CleanDatabase(dbPath, courseId);
            return new
            {
                courses = result.Courses,
                cleanedSubjects = result.SubjectsCleaned,
                ads = result.AdHits,
                charsRemoved = result.CharsRemoved
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 导入后清广告失败（忽略，导入结果不受影响）: {ex.Message}");
            return null;
        }
    }

    public static object GetStatus(AppPaths paths)
    {
        var sqlite = new FileInfo(paths.SqlitePath);
        var assets = Directory.Exists(paths.DataAssetsRoot) ? new DirectoryInfo(paths.DataAssetsRoot) : null;
        var userRoot = Directory.Exists(paths.UserDataRoot) ? new DirectoryInfo(paths.UserDataRoot) : null;
        var dataRoot = Directory.GetParent(paths.SqlitePath)?.FullName ?? paths.BaseRoot;
        var bankBackupRoot = Path.Combine(dataRoot, "_backups");
        var userBackupRoot = Path.Combine(paths.UserDataRoot, "_backups");
        return new
        {
            ok = true,
            bank = new
            {
                exists = sqlite.Exists,
                path = paths.SqlitePath,
                size = sqlite.Exists ? sqlite.Length : 0,
                updatedAt = sqlite.Exists ? sqlite.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") : "",
                tables = QuestionBank.TryReadTableCounts(paths.SqlitePath),
                assetsPath = paths.DataAssetsRoot,
                assetsFiles = assets is null ? 0 : CountFiles(assets.FullName),
                assetsSize = assets is null ? 0 : DirectorySize(assets.FullName)
            },
            userdata = new
            {
                path = paths.UserDataRoot,
                files = userRoot is null ? 0 : CountFiles(userRoot.FullName, path => !IsUnderBackup(path, userBackupRoot)),
                size = userRoot is null ? 0 : DirectorySize(userRoot.FullName, path => !IsUnderBackup(path, userBackupRoot)),
                users = userRoot is null ? 0 : AppHelpers.CountUserFiles(paths),
                auth = File.Exists(paths.AuthDataPath),
                updatedAt = LatestWriteTime(paths.UserDataRoot, path => !IsUnderBackup(path, userBackupRoot))
            },
            backups = new
            {
                bank = Directory.Exists(bankBackupRoot) ? Directory.EnumerateDirectories(bankBackupRoot).Count() : 0,
                userdata = Directory.Exists(userBackupRoot) ? Directory.EnumerateFiles(userBackupRoot, "*.zip", SearchOption.TopDirectoryOnly).Count() : 0
            }
        };
    }

    public static string CreateBankZip(AppPaths paths)
    {
        if (!File.Exists(paths.SqlitePath)) throw new InvalidOperationException("当前没有可下载的题库数据库");
        /* 下载也是一次"读活动题库"：
           · 必须排进写闸（拉取/导入/清广告正在换库/换图时导出，会拿到半个版本）；
           · 库的字节必须走 SQLite **在线备份**（直接压 live .db，WAL 里还没 checkpoint 的
             已提交事务会漏掉，拷到一半被写也会得到坏页）；
           · assets 与库在同一把闸内导出 = 同一版本。 */
        using var gate = BankWriteGate.Enter();
        var tempDir = Path.Combine(Path.GetTempPath(), $"yunxi-bankzip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempZip = Path.Combine(Path.GetTempPath(), $"yunxi-bank-{Guid.NewGuid():N}.zip");
        try
        {
            var snapshotDb = Path.Combine(tempDir, "question-bank.db");
            SqliteSnapshot.Copy(paths.SqlitePath, snapshotDb);
            using var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(snapshotDb, "question-bank.db", CompressionLevel.Fastest);
            zip.CreateEntry("assets/");
            if (Directory.Exists(paths.DataAssetsRoot))
            {
                AddDirectoryToZip(zip, paths.DataAssetsRoot, "assets");
            }
        }
        catch
        {
            TryDeleteFile(tempZip);
            throw;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
        return tempZip;
    }

    public static string CreateUserDataZip(AppPaths paths)
    {
        Directory.CreateDirectory(paths.UserDataRoot);
        var tempZip = Path.Combine(Path.GetTempPath(), $"yunxi-userdata-{Guid.NewGuid():N}.zip");
        var backupRoot = Path.Combine(paths.UserDataRoot, "_backups");
        using var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(paths.UserDataRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !IsUnderBackup(path, backupRoot))
                     .Where(path => !SessionStore.IsSessionFile(path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(paths.UserDataRoot, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
        }
        return tempZip;
    }

    public static object ImportBank(AppPaths paths, string uploadedFile, string originalName)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (ext != ".zip") throw new InvalidOperationException("题库上传仅支持 zip 包，包内需包含 question-bank.db 和 assets 目录");
        using var gate = BankWriteGate.Enter();
        var tempDir = Path.Combine(Path.GetTempPath(), $"yunxi-bank-{Guid.NewGuid():N}");
        var publishName = "bank-" + TimestampUnique();
        var stage = "";
        try
        {
            Directory.CreateDirectory(tempDir);
            ExtractZipSafe(uploadedFile, tempDir);
            var sourceDb = FindQuestionDb(tempDir) ?? throw new InvalidOperationException("压缩包中没有找到 question-bank.db / .sqlite 题库文件");
            var sourceAssets = FindAssetsDirectory(tempDir, sourceDb) ?? throw new InvalidOperationException("压缩包中没有找到 assets 目录");

            var dataRoot = Directory.GetParent(paths.SqlitePath)?.FullName ?? paths.BaseRoot;
            Directory.CreateDirectory(dataRoot);

            /* ── 阶段 1：在**同卷的版本目录**里准备好整套（DB + assets）并校验 ────────────
               线上文件在这一阶段一个字节都不动：复制/校验/清点失败的包不会碰到 live 库，
               更不会出现"新库配旧图"。旧实现是直接覆盖 live DB + 先删光 live assets 再复制，
               中途失败（磁盘满/IO 错/重启）就留下半套。 */
            stage = BankPublish.NewStagingDir(paths, publishName);
            var stagedDb = Path.Combine(stage, "question-bank.db");
            var stagedAssets = Path.Combine(stage, "assets");
            File.Copy(sourceDb, stagedDb, true);
            CopyDirectory(sourceAssets, stagedAssets);
            ValidateQuestionDb(stagedDb);
            if (!QuestionBank.ProbeHealthy(stagedDb))
            {
                throw new InvalidOperationException("上传的题库无法正常读取（缺少必要的表/列），已放弃本次导入，题库保持原样");
            }

            /* ── 阶段 2：发布前把本地改动重贴进新库 ─────────────────────────────
               只保留 local-edits.json 文件**不等于**改动已生效：整包替换会把主库整个换掉，
               这里对每门有编辑记录的课重贴（并刷新叠层里的"服务端快照"）。
               叠层读不了/写不了 → 抛 → 整个导入中止，上一版本原样保留（BE-09）。 */
            var reapplied = 0;
            foreach (var editedCourse in Puller.LocalEdits.CoursesWithEdits(paths.SqlitePath))
            {
                // 这门课新的主库里没有（未购课程在拉取库）→ 不归这条链路管，拉取时再贴
                if (!ServerBankPuller.MainHoldsCourseContent(stagedDb, editedCourse)) continue;
                Puller.LocalEdits.Reapply(paths.SqlitePath, stagedDb, editedCourse);
                reapplied++;
            }

            // 备份：库走 SQLite 在线备份（一致快照，WAL 里的事务不会漏），assets 照旧整棵留档
            var backupDir = Path.Combine(dataRoot, "_backups", publishName);
            Directory.CreateDirectory(backupDir);
            if (File.Exists(paths.SqlitePath)) SqliteSnapshot.Copy(paths.SqlitePath, Path.Combine(backupDir, "question-bank.db"));
            if (Directory.Exists(paths.DataAssetsRoot))
            {
                CopyDirectory(paths.DataAssetsRoot, Path.Combine(backupDir, "assets"));
            }
            BackupRetention.Prune(Path.Combine(dataRoot, "_backups"));

            /* ── 阶段 3：原子切换（同卷 rename；失败整套撤销，见 BankPublish.PublishBank） ── */
            SqliteConnection.ClearAllPools();   // 只是把空闲池连接放下，别让 rename 撞上句柄（不是一致性机制）
            BankPublish.PublishBank(paths, publishName, stagedDb, stagedAssets);

            return new
            {
                ok = true,
                type = "bank",
                message = "题库和图片资源已上传",
                backup = backupDir,
                localEditsReapplied = reapplied,
                // 导入路径也清广告（见 CleanAdsAfterImport 的说明）
                ads = CleanAdsAfterImport(paths.SqlitePath, 0),
                status = GetStatus(paths)
            };
        }
        finally
        {
            /* ⚠️ ValidateQuestionDb / TryReadTableCounts 开过的 SqliteConnection 会被**连接池**留着，
               池里的连接在 Windows 上占着解包目录/版本目录里那个 question-bank.db 的文件句柄，
               于是这里的 Directory.Delete 会抛「being used by another process」——
               上传明明已经成功（题库都换进去了），接口却回 400（实测 HEAD 上必现）。
               删目录前先把池清掉。 */
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            // 版本目录只在"还没切换完"时才有内容；切换成功后里面的东西已经被搬走了
            if (stage.Length > 0 && Directory.Exists(stage)) TryDeleteDirectory(stage);
        }
    }

    public static object ImportCourseBank(AppPaths paths, string uploadedFile, string originalName, int courseId)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (ext != ".zip") throw new InvalidOperationException("题库更新仅支持 zip 包，包内需包含 question-bank.db 和 assets 目录");
        using var gate = BankWriteGate.Enter();
        var tempDir = Path.Combine(Path.GetTempPath(), $"yunxi-course-bank-{Guid.NewGuid():N}");
        var publishName = "course-" + courseId + "-" + TimestampUnique();
        try
        {
            Directory.CreateDirectory(tempDir);
            ExtractZipSafe(uploadedFile, tempDir);
            var sourceDb = FindQuestionDb(tempDir) ?? throw new InvalidOperationException("压缩包中没有找到 question-bank.db / .sqlite 题库文件");
            var sourceAssets = FindAssetsDirectory(tempDir, sourceDb);

            ValidateQuestionDb(sourceDb);
            var sourceInfo = ReadCourseImportInfo(sourceDb, courseId);
            if (sourceInfo.Subjects <= 0) throw new InvalidOperationException($"压缩包中题库 ID {courseId} 没有可导入题目");

            var dataRoot = Directory.GetParent(paths.SqlitePath)?.FullName ?? paths.BaseRoot;
            Directory.CreateDirectory(dataRoot);

            /* ── 阶段 1：只准备**这门课**的 assets（版本目录，同卷） ──────────────
               源包里 assets 是按课程分目录的（assets/<courseId>/…）。旧实现把整棵 assets
               盖到线上：别的课的图也会被源包里那份旧图覆盖，任一张图 IO 失败还不撤销已经
               提交的库事务。这里只 stage 出这门课的那一份。 */
            string? stagedCourseAssets = null;
            if (sourceAssets is not null && Directory.Exists(Path.Combine(sourceAssets, courseId.ToString(CultureInfo.InvariantCulture))))
            {
                var stage = BankPublish.NewStagingDir(paths, publishName);
                stagedCourseAssets = Path.Combine(stage, "assets", courseId.ToString(CultureInfo.InvariantCulture));
                CopyDirectory(Path.Combine(sourceAssets, courseId.ToString(CultureInfo.InvariantCulture)), stagedCourseAssets);
            }

            // 备份：被修改的那一份库（这门课写的是主库）+ 这门课现有的图
            var backupDir = Path.Combine(dataRoot, "_backups", publishName);
            Directory.CreateDirectory(backupDir);
            if (File.Exists(paths.SqlitePath)) SqliteSnapshot.Copy(paths.SqlitePath, Path.Combine(backupDir, "question-bank.db"));
            var liveCourseAssets = Path.Combine(paths.DataAssetsRoot, courseId.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(liveCourseAssets))
            {
                CopyDirectory(liveCourseAssets, Path.Combine(backupDir, "assets", courseId.ToString(CultureInfo.InvariantCulture)));
            }
            BackupRetention.Prune(Path.Combine(dataRoot, "_backups"));

            QuestionBank.EnsureDatabase(paths.SqlitePath);
            BankPublish.CourseAssetPublish? publish = null;
            (int Chapters, int Subjects) result = (0, 0);
            var dbMerged = false;
            try
            {
                /* ── 阶段 2：assets 先就位（旧的一份挪到版本目录里，可逐字撤回） ─────── */
                if (stagedCourseAssets is not null)
                {
                    publish = BankPublish.PublishCourseAssets(paths, publishName, courseId, stagedCourseAssets);
                    publish.StampCourse(sourceInfo.Name, sourceInfo.Chapters, sourceInfo.Subjects);
                }

                /* ── 阶段 3：库合并（单个 SQLite 事务） ─────────────────────────── */
                result = MergeCourseIntoBank(paths.SqlitePath, sourceDb, courseId);
                dbMerged = true;

                /* ── 阶段 4：把这门课的本地改动重贴回去（导入也是"换掉正文"，本地优先） ── */
                Puller.LocalEdits.Reapply(paths.SqlitePath, paths.SqlitePath, courseId);

                publish?.Commit();
            }
            catch
            {
                /* 库合并**已经提交**之后的任何失败（重贴叠层 / assets 切换）→ 补偿回滚：
                   用刚做的在线备份把这门课整门贴回旧版本，assets 也退回上一版本。
                   旧实现这条链路上没有任何回滚：库已经换了，图是半套，界面却报失败。 */
                if (dbMerged) RollbackCourseImport(paths, backupDir, courseId);
                publish?.Rollback();
                throw;
            }

            return new
            {
                ok = true,
                type = "course-bank",
                courseId,
                courseName = sourceInfo.Name,
                chapters = result.Chapters,
                subjects = result.Subjects,
                message = $"题库已更新：{sourceInfo.Name}（{result.Chapters} 章，{result.Subjects} 题）",
                backup = backupDir,
                // 导入路径也清广告，只扫这门课（整库别动：别的课可能正在被用）
                ads = CleanAdsAfterImport(paths.SqlitePath, courseId),
                results = new[] { new { courseId, chapters = result.Chapters, subjects = result.Subjects } },
                status = GetStatus(paths)
            };
        }
        finally
        {
            // 同 ImportBank：连接池占着解包目录里的库文件，先清池再删目录
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            TryDeleteDirectory(Path.Combine(BankPublish.VersionsRoot(paths), publishName));
        }
    }

    /// <summary>
    /// 单课导入失败后的补偿回滚：把这门课从导入前的在线备份里整门贴回来。
    /// 导入前库里没有这门课（新导入的课）→ 直接删掉。
    /// </summary>
    private static void RollbackCourseImport(AppPaths paths, string backupDir, int courseId)
    {
        var backupDb = Path.Combine(backupDir, "question-bank.db");
        if (!File.Exists(backupDb))
        {
            Console.WriteLine($"[course-import-rollback] 没有可用的备份库（{backupDb}），课程 {courseId} 保持导入后的内容");
            return;
        }
        try
        {
            if (CourseExists(backupDb, courseId))
            {
                MergeCourseIntoBank(paths.SqlitePath, backupDb, courseId);
            }
            else
            {
                DeleteCourse(paths.SqlitePath, courseId);
            }
            Console.WriteLine($"[course-import-rollback] 课程 {courseId} 已从备份 {backupDir} 回滚到导入前");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"导入失败后自动回滚课程 {courseId} 也失败了（{ex.Message}）；请用备份 {backupDir} 手工恢复", ex);
        }
    }

    private static bool CourseExists(string dbPath, int courseId)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath), Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 30,
        }.ToString());
        conn.Open();
        return ExecuteScalar(conn, "select 1 from course where icourseid=@courseId", new Dictionary<string, object?> { ["@courseId"] = courseId }) is not null;
    }

    /// <summary>删掉一门课（导入前库里本来没有这门课时，回滚就是把它删干净）。</summary>
    private static void DeleteCourse(string targetDb, int courseId)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(targetDb), DefaultTimeout = 30 }.ToString());
        conn.Open();
        using var transaction = conn.BeginTransaction();
        ExecuteNonQuery(conn, transaction, "delete from coursesubject where icourseid=@courseId", new Dictionary<string, object?> { ["@courseId"] = courseId });
        ExecuteNonQuery(conn, transaction, "delete from coursechapter where icourseid=@courseId", new Dictionary<string, object?> { ["@courseId"] = courseId });
        ExecuteNonQuery(conn, transaction, "delete from course where icourseid=@courseId", new Dictionary<string, object?> { ["@courseId"] = courseId });
        transaction.Commit();
    }

    public static object ImportUserData(AppPaths paths, AuthStore auth, UserDataStore userData, string uploadedFile, string originalName)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (ext != ".zip") throw new InvalidOperationException("用户数据上传仅支持 zip 包");
        using var gate = BankWriteGate.Enter();
        var tempDir = Path.Combine(Path.GetTempPath(), $"yunxi-userdata-{Guid.NewGuid():N}");
        var importDir = Path.Combine(tempDir, "import");
        try
        {
            Directory.CreateDirectory(importDir);
            ExtractZipSafe(uploadedFile, importDir);

            var files = CollectUserDataFiles(importDir).ToList();
            if (files.Count == 0) throw new InvalidOperationException("没有找到可导入的用户数据文件");
            foreach (var file in files) ValidateJsonFile(file.Path, file.Name);
            var accountsFile = files.FirstOrDefault(file => string.Equals(file.Name, "accounts.dat", StringComparison.OrdinalIgnoreCase));
            var hasAccounts = accountsFile.Path is not null;
            // 结构校验：合法 JSON 但结构错（{"admin":"oops"} / []）会让凭据被静默清空
            if (hasAccounts) ValidateAccountsFile(accountsFile.Path!);

            Directory.CreateDirectory(paths.UserDataRoot);
            var backupRoot = Path.Combine(paths.UserDataRoot, "_backups");
            Directory.CreateDirectory(backupRoot);
            var backupZip = Path.Combine(backupRoot, "userdata-" + TimestampUnique() + ".zip");
            using (var zip = ZipFile.Open(backupZip, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(paths.UserDataRoot, "*", SearchOption.AllDirectories)
                             .Where(path => !IsUnderBackup(path, backupRoot))
                             .Where(path => !SessionStore.IsSessionFile(path)))
                {
                    var relative = Path.GetRelativePath(paths.UserDataRoot, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }
            }
            BackupRetention.Prune(backupRoot);

            /* ── 提交阶段：可回滚的「备份 → 就位」两阶段 ──────────────────────────────
               旧实现的提交是「先把线上 *.json 全删光 + 删 accounts.dat，再逐个 Move」，
               catch 里只删 temp。于是任何一次 Move 失败，用户数据已经被删掉且**永不恢复**
               （备份 zip 在，但没有任何自动回滚）—— 实测一次注入失败就让 u1.json/u2.json
               永久消失，/api/admin/users 从 u1,u2 变成只剩新导入的那个。

               新做法把"破坏性动作"全部变成**可撤销**的：
                 阶段 1  把将被覆盖/将被替换的现有文件**移动到撤销目录**（同卷 rename，不会失败）；
                         新增用户（目标不存在）不在此列，撤销时直接删掉即可。
                 阶段 2  逐个把已 staged 的 temp Move 到最终名。每成功一个就记进 placed。
                 撤销     任一步抛异常 → 先把 placed 里的新文件删掉，再把撤销目录里的旧文件
                         原样 Move 回去（顺带还原原文件的修改时间/属性由 rename 天然保留）。

               为什么选「移到撤销目录」而不是注入失败后从备份 zip 解：走 zip 要重新解包
               整个用户目录、会丢扩展属性、还要处理 zip 里没有的会话文件；rename 是 O(1)
               且语义精确。撤销目录放在**同一卷**（UserDataRoot 下），保证 File.Move 是
               同卷改名而不是跨卷拷贝（跨卷拷贝会慢且可能中途失败）。

               另外：`sessions.json` 不在删除/替换范围内（它是登录会话存储，不是用户），
               `_backups/` 也不会被碰到 —— 这两条由 CollectUserDataFiles 的保留名校验先兜住。

               ⚠️ 提交阶段同时接受**按用户锁**：UserDataStore 提供 RunExclusive，
               我们对包内每个用户都取一次锁，把"这个用户的数据"串行化。
               不这样的话会与用户正在进行的 load/save 交错：用户读到旧内容 →
               管理员的导入把文件整批换掉 → 用户基于旧快照 save，把导入结果覆盖回去。 */
            var rollbackDir = Path.Combine(paths.UserDataRoot, ".import-rollback-" + Guid.NewGuid().ToString("N"));
            // 包内每个用户各取一次锁（去重，避免同一用户重复取锁自锁）。
            var lockUsers = files.Select(f => Path.GetFileNameWithoutExtension(f.Name))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var staged = new List<(string Temp, string Final)>();
            var placed = new List<string>();
            var displaced = new List<(string Saved, string Final)>();
            try
            {
                Directory.CreateDirectory(rollbackDir);
                foreach (var file in files)
                {
                    var final = Path.Combine(paths.UserDataRoot, file.Name);
                    var temp = Path.Combine(rollbackDir, ".stage-" + Guid.NewGuid().ToString("N"));
                    File.Copy(file.Path, temp, true);
                    RestrictFilePermissions(temp);
                    staged.Add((temp, final));
                }

                // 取锁 → 提交 → 释放。锁的获取顺序固定（按名字排序），避免两个导入互相死锁。
                CommitUnderUserLocks(userData, lockUsers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), () =>
                {
                    // 阶段 1：把「会被覆盖」和「会被删掉」的现有文件移进撤销目录。
                    // 用户 json：导入是**整包替换语义**（包里的用户集合成为新集合），所以不在
                    // 包里的旧用户 json 也要一并撤走 —— 撤销时再原样移回。
                    Directory.CreateDirectory(paths.UserDataRoot);
                    foreach (var old in Directory.EnumerateFiles(paths.UserDataRoot, "*.json", SearchOption.TopDirectoryOnly)
                                 .Where(path => !SessionStore.IsSessionFile(path)))
                    {
                        var saved = Path.Combine(rollbackDir, "old-" + Guid.NewGuid().ToString("N") + ".json");
                        File.Move(old, saved);
                        displaced.Add((saved, old));
                    }
                    if (hasAccounts && File.Exists(paths.AuthDataPath))
                    {
                        var savedAuth = Path.Combine(rollbackDir, "old-accounts.dat");
                        File.Move(paths.AuthDataPath, savedAuth);
                        displaced.Add((savedAuth, paths.AuthDataPath));
                    }

                    // 阶段 2：就位。每成功一个 Move 立刻登记，撤销时才知道该删哪些。
                    foreach (var (temp, final) in staged)
                    {
                        File.Move(temp, final, true);
                        placed.Add(final);
                    }
                });
            }
            catch
            {
                /* 撤销：必须**尽力而为、逐项 try**。若这里因为同一原因再抛一次，
                   catch 会盖掉原始异常，并且撤销半途而废。先把撤销做完，再重抛原始异常。 */
                foreach (var final in placed) TryDeleteFile(final);
                foreach (var (saved, final) in displaced)
                {
                    try
                    {
                        if (File.Exists(saved)) File.Move(saved, final, true);
                    }
                    catch
                    {
                        // 撤销回到该文件失败：记录，便于人工从 backup zip 恢复。绝不吞掉原始异常。
                        Console.WriteLine($"[userdata-import-rollback-failed] {saved} -> {final}");
                    }
                }
                throw;
            }
            finally
            {
                foreach (var (temp, _) in staged) TryDeleteFile(temp);
                TryDeleteDirectory(rollbackDir);
            }
            auth.Reload();
            return new
            {
                ok = true,
                type = "userdata",
                message = $"用户数据已上传：{files.Count} 个文件",
                backup = backupZip,
                status = GetStatus(paths)
            };
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// 在**多个用户的按用户锁**下执行一次提交。
    ///
    /// 导入是整包替换：一次要动多个用户的文件，所以要把涉及的用户全部先锁住再动手，
    /// 中途逐个"边锁边写"会留下"一部分用户已换、一部分还没换"的中间态。
    /// 调用方保证 <paramref name="users"/> 已按固定顺序去重排序 —— 两个并发导入
    /// 若按不同顺序取锁就会互相死锁。
    /// </summary>
    private static void CommitUnderUserLocks(UserDataStore userData, List<string> users, Action commit)
    {
        if (users.Count == 0)
        {
            commit();
            return;
        }
        CommitFrom(userData, users, 0, commit);
    }

    private static void CommitFrom(UserDataStore userData, List<string> users, int index, Action commit)
    {
        if (index >= users.Count)
        {
            commit();
            return;
        }
        userData.RunExclusive(users[index], () =>
        {
            CommitFrom(userData, users, index + 1, commit);
            // RunExclusive 的返回值只是为了让泛型能推导，这里没有实际值要带出来
            return true;
        });
    }

    /// <summary>
    /// 收集包内可导入的用户数据文件，并把它**映射到用户数据目录下的目标文件名**。
    ///
    /// ⚠️ 两条硬约束，都是踩过的坑：
    /// 1. **撞名必须报错，不能静默覆盖**。目标名由 CleanUserName 归一化而来，是**多对一**的：
    ///    包里的 `胡 平.json`、`胡_平.json`、`胡/平.json` 都会落到同一个 `胡_平.json`。
    ///    旧实现直接 yield，后一个把前一个静默覆盖 —— 上传返回 200、文件数也"对"，
    ///    但有一个用户的数据被无声吞掉，事后无从察觉。这里显式抛出，让管理员改名重传。
    /// 2. **保留名要拒绝**。`accounts.dat`/`sessions.json`/`_backups/…` 不是用户数据：
    ///    · `sessions.json` 是登录会话存储，包内同名文件会**顶掉线上会话**（等于上传者凭空
    ///      获得/破坏他人登录态）；它同时也在下面的"删除"范围里，必须先于任何写操作挡掉。
    ///    · `_backups/…` 会往备份目录里塞任意内容，破坏备份保留策略。
    /// </summary>
    private static List<(string Path, string Name)> CollectUserDataFiles(string root)
    {
        var collected = new List<(string Path, string Name)>();
        var byTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            // 包内相对路径（用于报错信息，让管理员知道是哪个文件）
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (name.Equals("accounts.dat", StringComparison.OrdinalIgnoreCase))
            {
                if (byTarget.TryGetValue("accounts.dat", out var dupAccounts))
                {
                    throw new InvalidOperationException($"压缩包里有多个 accounts.dat（{dupAccounts} 与 {relative}），无法确定采用哪一个");
                }
                byTarget["accounts.dat"] = relative;
                collected.Add((file, "accounts.dat"));
                continue;
            }
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            var cleanName = CleanUserName(Path.GetFileNameWithoutExtension(name)) + ".json";
            /* 保留名检查要看**两个**东西：
               ① 目标文件名本身（`sessions.json` 归一化后还是 `sessions.json`）；
               ② 包内**源路径**里有没有保留目录段（`_backups/x.json`、`p/_backups/x.json`）。
                  只查①是不够的：CollectUserDataFiles 用 SearchOption.AllDirectories 递归收，
                  但目标名只取 `Path.GetFileName`，所以 `_backups/evil.json` 会被**摊平**成
                  顶层的 `evil.json` —— 变成一个凭空多出来的用户，而不是落进备份目录。
                  （实测：包内 `_backups/evil.json` 导入后 /api/admin/users 多出 evil 账号。） */
            if (IsReservedUserDataFile(cleanName) || IsReservedUserDataFile(relative))
            {
                throw new InvalidOperationException($"压缩包里的 {relative} 是系统保留文件/目录，不能导入");
            }
            if (byTarget.TryGetValue(cleanName, out var dup))
            {
                throw new InvalidOperationException(
                    $"压缩包里的 {dup} 与 {relative} 会归一化成同一个用户文件名「{cleanName}」，请改名后重新上传");
            }
            byTarget[cleanName] = relative;
            collected.Add((file, cleanName));
        }
        return collected;
    }

    /// <summary>
    /// 用户数据目录里**不属于用户**的文件名/路径：会话存储与备份目录。
    /// 导入时它们既不能被覆盖，也不能进"会被清掉的旧文件"名单。
    /// 参数可以是纯文件名（`sessions.json`）或包内相对路径（`_backups/x.json`）。
    /// </summary>
    private static bool IsReservedUserDataFile(string nameOrRelative)
    {
        var slash = nameOrRelative.Replace('\\', '/').TrimStart('/');
        // 任何一段是 _backups 就拒绝：包内 `_backups/x.json` 会被摊平成顶层 `x.json`，
        // 只看文件名挡不住，必须看源路径里的目录段。
        foreach (var segment in slash.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("_backups", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return SessionStore.IsSessionFile(Path.GetFileName(slash));
    }

    private static void ValidateJsonFile(string path, string name)
    {
        try
        {
            using var stream = File.OpenRead(path);
            JsonDocument.Parse(stream);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{name} 不是有效 JSON：{ex.Message}");
        }
    }

    /// <summary>
    /// 校验题库包：必须能跑通**读路径的真实查询**。
    /// 以前只对 4 张表做 count(*)，"表在但列不全"的包会通过校验，上传成功后 /api/courses 直接 500。
    /// </summary>
    private static void ValidateQuestionDb(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 30 }.ToString());
        conn.Open();
        // 列清单必须和读路径的真实 SELECT 对齐（QuestionBank 的各 Get* 方法）。
        // 探针漏一列，缺该列的旧包就能通过校验，上传成功之后接口才 500。
        // 对齐口径：读路径全部 SQL（含 JOIN / WHERE / ORDER BY）引用到的列并集；
        // 探针自身 JOIN 键/WHERE 已覆盖的列不重复列（例如 c.iclassid、s.isubjecttype）。
        foreach (var sql in new[]
        {
            // course / courseclass / coursesubclass ← GetCourses（ORDER BY c.iindex，WHERE coalesce(c.bstopflag,0)）
            "select c.icourseid, c.ccoursename, c.ihadbuy, c.dchangedate, c.dchapterchange, c.dsubjectchange, c.iindex, c.bstopflag, cl.ccoursecname, cl.iindex, sc.csubclassname, sc.iindex from course c left join courseclass cl on c.iclassid=cl.iclassid left join coursesubclass sc on c.isubclassid=sc.isubclassid limit 1",
            // coursechapter ← GetChapters / GetChapterPath / BuildChapterMap（WHERE icourseid、coalesce(bstopflag,0)）
            "select ch.ichapterid, ch.icourseid, ch.cchaptername, ch.cchaptercode, ch.igrade, ch.itype, ch.icount, ch.bstopflag from coursechapter ch limit 1",
            // coursesubject ← GetQuestions / GetQuestion / GetQuestionChapterPairs / GetEditorQuestions / GetEditorQuestion / BuildChapterMap
            // （s.bstopflag / s.ichaptertype 只见于读路径的 WHERE、ORDER BY，但缺列同样会 500，所以也要进 SELECT 清单）
            "select s.isubjectid, s.icourseid, s.ichapterid, s.isubjecttype, s.iindex, s.ichaptertype, s.iscore, s.ctitle, s.cquestion, s.canswer, s.cdescription, s.ianswercount, s.dupdatedate, s.bstopflag, t.csubjectname, ch.cchaptername from coursesubject s left join coursesubjecttype t on s.isubjecttype=t.isubjecttype left join coursechapter ch on s.ichapterid=ch.ichapterid limit 1",
            // coursesubjecttype 的列（isubjecttype / csubjectname）已被上面那条的 JOIN 与 t.csubjectname 覆盖，这里只验表在不在
            "select count(*) from coursesubjecttype",
        })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteScalar();
        }
    }

    /// <summary>
    /// 校验 accounts.dat：必须是「账号名 → 档案对象」的 JSON 对象，且含 admin。
    /// 以前只验"是不是 JSON"，结构错的合法 JSON（`{"admin":"oops"}` / `[]`）会通过校验，
    /// 导入后凭据被静默清空、重启后无人能登录。
    /// 现在还要求 **至少有一个可用的管理员**：导入一个"有账号但 admin 档案残缺
    /// （没有 salt/hash）"的包，会通过上面的结构校验，但导入后没人能登录，
    /// 只能靠初始口令兜底 —— 那条路径会顺手把凭据重写一遍，等于把坏包固化。
    /// </summary>
    private static void ValidateAccountsFile(string path)
    {
        Dictionary<string, Dictionary<string, object?>>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object?>>>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"accounts.dat 结构不正确（应为「账号名 → 档案对象」的对象）：{ex.Message}");
        }
        if (raw is null || raw.Count == 0) throw new InvalidOperationException("accounts.dat 里没有任何账号");

        // 每个档案值都必须是非 null 对象（`{"admin":null}` 反序列化成 null 值，字典校验不到）
        foreach (var pair in raw)
        {
            if (pair.Value is null)
            {
                throw new InvalidOperationException($"accounts.dat 里账号「{pair.Key}」的档案是 null，结构不正确");
            }
        }

        // 至少一个可用的 admin：档案齐全（salt/hash）且没被停用
        var admin = raw.FirstOrDefault(pair => pair.Key.Equals("admin", StringComparison.OrdinalIgnoreCase));
        if (admin.Key is null)
        {
            throw new InvalidOperationException("accounts.dat 里没有 admin 账号，导入后将无人能登录管理后台");
        }
        var profile = admin.Value;
        var hasSalt = profile.TryGetValue("salt", out var saltValue) && ScalarString(saltValue).Length > 0;
        var hasHash = profile.TryGetValue("hash", out var hashValue) && ScalarString(hashValue).Length > 0;
        if (!hasSalt || !hasHash)
        {
            throw new InvalidOperationException("accounts.dat 里 admin 的档案缺少 salt/hash，导入后无法登录");
        }
        if (profile.TryGetValue("disabled", out var disabledValue) && ScalarBool(disabledValue))
        {
            throw new InvalidOperationException("accounts.dat 里 admin 被标记为停用，导入后将无人能登录管理后台");
        }
    }

    /// <summary>课程名 + 章节/题目数（发布日志据此判断"导入到底落库没有"）。</summary>
    internal static (string Name, int Chapters, int Subjects) ReadCourseImportInfo(string dbPath, int courseId)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 30 }.ToString());
        conn.Open();
        var name = Convert.ToString(ExecuteScalar(conn, "select ccoursename from course where icourseid=@courseId", new Dictionary<string, object?> { ["@courseId"] = courseId }), CultureInfo.InvariantCulture) ?? "";
        if (name.Length == 0) throw new InvalidOperationException($"压缩包中没有找到题库 ID {courseId}");
        var chapters = Convert.ToInt32(ExecuteScalar(conn, "select count(*) from coursechapter where icourseid=@courseId and coalesce(bstopflag,0)=0", new Dictionary<string, object?> { ["@courseId"] = courseId }), CultureInfo.InvariantCulture);
        var subjects = Convert.ToInt32(ExecuteScalar(conn, "select count(*) from coursesubject where icourseid=@courseId and coalesce(bstopflag,0)=0", new Dictionary<string, object?> { ["@courseId"] = courseId }), CultureInfo.InvariantCulture);
        return (name, chapters, subjects);
    }

    private static (int Chapters, int Subjects) MergeCourseIntoBank(string targetDb, string sourceDb, int courseId)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetDb, DefaultTimeout = 30 }.ToString());
        conn.Open();
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "attach database @source as src";
            attach.Parameters.AddWithValue("@source", sourceDb);
            attach.ExecuteNonQuery();
        }
        try
        {
            using (var transaction = conn.BeginTransaction())
            {
                var sourceInfo = ReadCourseImportInfo(sourceDb, courseId);
                ExecuteNonQuery(conn, transaction, "delete from coursesubject where icourseid=@courseId", new Dictionary<string, object?> { ["@courseId"] = courseId });
                ExecuteNonQuery(conn, transaction, "delete from coursechapter where icourseid=@courseId", new Dictionary<string, object?> { ["@courseId"] = courseId });
                ExecuteNonQuery(conn, transaction, "delete from course where icourseid=@courseId", new Dictionary<string, object?> { ["@courseId"] = courseId });

                CopyRows(conn, transaction, "src.courseclass", "courseclass", "iclassid in (select iclassid from src.course where icourseid=@courseId)", courseId);
                CopyRows(conn, transaction, "src.coursesubclass", "coursesubclass", "isubclassid in (select isubclassid from src.course where icourseid=@courseId)", courseId);
                CopyRows(conn, transaction, "src.coursesubjecttype", "coursesubjecttype", "1=1", courseId);
                CopyRows(conn, transaction, "src.course", "course", "icourseid=@courseId", courseId);
                CopyRows(conn, transaction, "src.coursechapter", "coursechapter", "icourseid=@courseId", courseId);
                CopyRows(conn, transaction, "src.coursesubject", "coursesubject", "icourseid=@courseId", courseId);

                transaction.Commit();
                return (sourceInfo.Chapters, sourceInfo.Subjects);
            }
        }
        finally
        {
            using var detach = conn.CreateCommand();
            detach.CommandText = "detach database src";
            detach.ExecuteNonQuery();
        }
    }

    private static void CopyRows(SqliteConnection conn, SqliteTransaction transaction, string sourceTable, string targetTable, string whereSql, int courseId)
    {
        var targetColumns = GetTableColumns(conn, transaction, "", targetTable);
        var sourceColumns = GetTableColumns(conn, transaction, sourceTable.Contains('.') ? sourceTable.Split('.')[0] : "", sourceTable.Contains('.') ? sourceTable.Split('.')[1] : sourceTable);
        var columns = targetColumns.Intersect(sourceColumns, StringComparer.OrdinalIgnoreCase).ToList();
        if (columns.Count == 0) throw new InvalidOperationException($"无法导入表 {targetTable}，没有匹配字段");
        var columnSql = string.Join(", ", columns.Select(QuoteIdent));
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"insert or replace into {QuoteIdent(targetTable)} ({columnSql}) select {columnSql} from {sourceTable} where {whereSql}";
        cmd.Parameters.AddWithValue("@courseId", courseId);
        cmd.ExecuteNonQuery();
    }

    private static List<string> GetTableColumns(SqliteConnection conn, SqliteTransaction transaction, string schema, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = string.IsNullOrWhiteSpace(schema)
            ? $"pragma table_info({QuoteIdent(table)})"
            : $"pragma {QuoteIdent(schema)}.table_info({QuoteIdent(table)})";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(Convert.ToString(reader["name"], CultureInfo.InvariantCulture) ?? "");
        }
        return result.Where(item => item.Length > 0).ToList();
    }

    private static object? ExecuteScalar(SqliteConnection conn, string sql, Dictionary<string, object?> parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var pair in parameters) cmd.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private static void ExecuteNonQuery(SqliteConnection conn, SqliteTransaction transaction, string sql, Dictionary<string, object?> parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var pair in parameters) cmd.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string QuoteIdent(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string? FindQuestionDb(string root)
    {
        var dbFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".db" or ".sqlite" or ".sqlite3")
            .ToList();
        return dbFiles.OrderByDescending(path => Path.GetFileName(path).Equals("question-bank.db", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    private static string? FindAssetsDirectory(string root, string dbPath)
    {
        var candidates = Directory.EnumerateDirectories(root, "assets", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Replace('\\', '/').Contains("/data/assets", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Length)
            .ToList();
        if (candidates.Count > 0) return candidates[0];
        var sibling = Path.Combine(Path.GetDirectoryName(dbPath) ?? root, "assets");
        return Directory.Exists(sibling) ? sibling : null;
    }

    private static void AddDirectoryToZip(ZipArchive zip, string root, string entryRoot)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, entryRoot.TrimEnd('/') + "/" + relative, CompressionLevel.Fastest);
        }
    }

    private static void ExtractZipSafe(string zipPath, string destination)
    {
        const long maxExpandedBytes = 4L * 1024 * 1024 * 1024;
        const long maxEntryBytes = 1024L * 1024 * 1024;
        const int maxEntries = 100_000;
        var fullDestination = Path.GetFullPath(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > maxEntries) throw new InvalidOperationException($"压缩包文件数量超过 {maxEntries}");
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > maxEntryBytes) throw new InvalidOperationException("压缩包包含超过 1GB 的单个文件");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > maxExpandedBytes) throw new InvalidOperationException("压缩包解压后超过 4GB 限制");
            var clean = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (clean.Length == 0 || clean.Contains("../", StringComparison.Ordinal) || clean.StartsWith("..", StringComparison.Ordinal))
            {
                continue;
            }
            var target = Path.GetFullPath(Path.Combine(fullDestination, clean));
            if (!target.StartsWith(fullDestination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, fullDestination, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("压缩包包含非法路径");
            }
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetFile = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, true);
        }
    }

    private static long DirectorySize(string root, Func<string, bool>? include = null)
    {
        if (!Directory.Exists(root)) return 0;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => include?.Invoke(path) ?? true)
            .Sum(path => new FileInfo(path).Length);
    }

    private static int CountFiles(string root, Func<string, bool>? include = null)
    {
        if (!Directory.Exists(root)) return 0;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Count(path => include?.Invoke(path) ?? true);
    }

    private static string LatestWriteTime(string root, Func<string, bool>? include = null)
    {
        if (!Directory.Exists(root)) return "";
        var latest = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => include?.Invoke(path) ?? true)
            .Select(path => File.GetLastWriteTime(path))
            .DefaultIfEmpty()
            .Max();
        return latest == default ? "" : latest.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static bool IsUnderBackup(string path, string backupRoot)
    {
        if (string.IsNullOrWhiteSpace(backupRoot)) return false;
        var fullPath = Path.GetFullPath(path);
        var fullBackup = Path.GetFullPath(backupRoot);
        return fullPath.StartsWith(fullBackup + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, fullBackup, StringComparison.OrdinalIgnoreCase);
    }
}
