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

static class ServerBankPuller
{
    public static object PullCourseIntoBank(AppPaths paths, IConfiguration configuration, int courseId)
    {
        if (courseId <= 0) throw new InvalidOperationException("请选择要更新的题库");
        var live = Path.GetFullPath(paths.SqlitePath);

        /* 进度登记必须在取闸**之前**：BankWriteGate 门口最长要等 15 分钟，
           等待期间没有记录的话，前端轮询到的是"这门课没有数据"（用户反馈 1）。
           闸的排队位次由 Enter 在开始阻塞前回调进来（MarkQueued → "前面还有 N 个任务"）。 */
        using var progressScope = Puller.PullProgressTracker.Shared.Begin(courseId, ReadCourseName(live, courseId));
        try
        {
            // 与整包上传/单课导入/清广告互斥（都是"先删后写"，交叠会把课程清成 0 题）
            using var gate = BankWriteGate.Enter(
                ahead => Puller.PullProgressTracker.Shared.MarkQueued(courseId, ahead));
            var result = PullCourseUnderGate(paths, configuration, courseId, live);
            // 拉取 + 收尾（重贴本地改动 / 对比报告）都走完了才算「完成」
            progressScope.Complete();
            return result;
        }
        catch (Exception ex)
        {
            /* 兜底收尾：任何异常都要把这条进度记录标成失败再抛。
               以前只有「拉取本身」被 try 包住，后面那一段（重贴本地改动 / 读写题号 / 打日志）
               是裸奔的 —— 在那里抛异常就没人调 Complete()，记录永远 active=true，
               界面一直显示"正在更新"，用户看到的就是"更新完成后还显示更新中"（用户反馈 2）。 */
            Puller.PullProgressTracker.Shared.Fail(courseId, SafeErrorText(ex));
            throw;
        }
    }

    /// <summary>
    /// 拉取主体。调用方必须已经持有 <see cref="BankWriteGate"/> 的闸 ——
    /// 这里全是"先删后写"，并发交叠会把课程清成 0 题。
    /// <paramref name="live"/> 是主库路径（进度登记时已经取过，见 <see cref="PullCourseIntoBank"/>）。
    /// </summary>
    private static object PullCourseUnderGate(AppPaths paths, IConfiguration configuration, int courseId, string live)
    {
        // 课程内容在主库（导出产物里有正文）→ 就地更新主库；否则（未购课程）→ 写**拉取库**。
        // 拉取库不在"整包上传题库"的替换范围内，所以拉来的内容不会被那条链路清掉。
        var intoPulled = !MainHoldsCourseContent(live, courseId);
        var target = intoPulled ? Path.GetFullPath(paths.PulledSqlitePath) : live;
        var assetsRoot = intoPulled ? paths.PulledAssetsRoot : paths.DataAssetsRoot;

        var dataRoot = Directory.GetParent(paths.SqlitePath)?.FullName ?? paths.BaseRoot;
        var backupRoot = Path.Combine(dataRoot, "_backups");
        var backupDir = Path.Combine(backupRoot, "pull-course-" + courseId + "-" + TimestampUnique());
        Directory.CreateDirectory(backupDir);
        if (File.Exists(target))
        {
            // 在线备份：直接 File.Copy 活动库会漏掉 WAL 里还没 checkpoint 的已提交事务（BE-08）
            SqliteSnapshot.Copy(target, Path.Combine(backupDir, Path.GetFileName(target)));
        }
        // Only this course's images are rewritten by a pull, so only those are snapshotted —
        // copying the whole assets tree (every course) would be needlessly expensive.
        var courseAssets = Path.Combine(assetsRoot, courseId.ToString(CultureInfo.InvariantCulture));
        if (Directory.Exists(courseAssets))
        {
            CopyTree(courseAssets, Path.Combine(backupDir, "assets", courseId.ToString(CultureInfo.InvariantCulture)));
        }
        BackupRetention.Prune(backupRoot);

        QuestionBank.EnsureDatabase(paths.SqlitePath);

        // 覆盖前记下这门课的章节/题号，拉完做"新增 / 消失"对比（原"题库管理器"的预检并入这里）
        var before = ReadCourseIds(target, courseId);

        // 写拉取库时要把课程行与三张字典表从主库拷过去（SQLite 不能 ATTACH 自己，所以主库就地更新时 metaDb 为 null）。
        var options = Puller.BankPull.CourseOptions(
            courseId,
            target,
            assetsRoot,
            metaDb: intoPulled ? (File.Exists(live) ? live : null) : null,
            serviceUrl: configuration["App:BankServiceUrl"],
            user: configuration["App:BankServiceUser"],
            password: configuration["App:BankServicePassword"]);

        // 进度登记在取闸前就做完了（见 PullCourseIntoBank），这里只上报拉取本身的阶段。
        // POST 仍是同步返回最终结果（前端契约不变）。
        var summary = Puller.BankPull
            .PullCourseAsync(options,
                line => Console.WriteLine($"[bankpull] {line}"),
                point => Puller.PullProgressTracker.Shared.Report(courseId, point))
            .GetAwaiter()
            .GetResult();

        // 本地改动（题库编辑器改过的题干/解析/章节名）重贴回去：本地优先
        /* 叠层读不了/写不了时**不能**装作"没有本地改动"（旧实现：读失败返回空叠层、
           写失败静默吞掉，拉完编辑就悄悄消失）。这里把它作为**部分失败**明确报出去：
           题库本身是好的（ok 仍为 true），但本地改动没有贴回来，界面要能看到。 */
        Puller.ReapplyOutcome localEdits;
        string? localEditsError = null;
        try
        {
            localEdits = Puller.LocalEdits.Reapply(paths.SqlitePath, target, courseId);
        }
        catch (Puller.LocalEditsUnavailableException ex)
        {
            localEdits = new Puller.ReapplyOutcome(0, 0, 0);
            localEditsError = SafeErrorText(ex);
        }

        // 拉完再读一次，得出"新增 / 消失"的题号
        var after = ReadCourseIds(target, courseId);
        var added = after.Subjects.Except(before.Subjects).ToList();
        var removed = before.Subjects.Except(after.Subjects).ToList();

        var courseName = ReadCourseName(target, courseId);
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 题库拉取完成：课程 {courseId} " +
                          $"{summary.Chapters}章/{summary.Subjects}题/{summary.Images}图，" +
                          $"清洗广告 {summary.AdHits} 处，{localEdits.Describe()}，" +
                          $"新增 {added.Count} 题 / 消失 {removed.Count} 题，" +
                          $"用时 {summary.ElapsedSeconds}s，写入 {(intoPulled ? "拉取库" : "主库")}");

        return new
        {
            ok = true,
            type = "course-bank",
            mode = "pull",
            courseId,
            courseName,
            target = intoPulled ? "pulled" : "main",
            chapters = summary.Chapters,
            subjects = summary.Subjects,
            images = summary.Images,
            imagesFailed = summary.ImagesFailed,
            ads = new
            {
                subjects = summary.AdSubjectsCleaned,
                hits = summary.AdHits,
                charsRemoved = summary.AdCharsRemoved,
            },
            soapCalls = summary.SoapCalls,
            elapsedSeconds = summary.ElapsedSeconds,
            localEditsReapplied = localEdits.Subjects + localEdits.Chapters,
            localEditsError = localEditsError,
            partial = localEditsError is not null,
            message = localEditsError is null
                ? summary.Describe()
                : summary.Describe() + $"；但本地改动**没有**重贴：{localEditsError}",
            backup = backupDir,
            results = new[] { new { courseId, chapters = summary.Chapters, subjects = summary.Subjects } },
            // 对比报告（原"题库管理器"的预检）：新增/消失的题与章节，以及本地改动受影响的情况
            report = new
            {
                before = new { chapters = before.Chapters.Count, subjects = before.Subjects.Count },
                after = new { chapters = after.Chapters.Count, subjects = after.Subjects.Count },
                addedSubjects = added.Count,
                removedSubjects = removed.Count,
                addedSubjectSample = added.Take(8).ToList(),
                removedSubjectSample = removed.Take(8).ToList(),
                addedChapters = after.Chapters.Except(before.Chapters).Count(),
                removedChapters = before.Chapters.Except(after.Chapters).Count(),
                localEdits = new
                {
                    subjects = localEdits.Subjects,
                    chapters = localEdits.Chapters,
                    orphans = localEdits.Orphans,
                    text = localEdits.Describe(),
                },
            },
            status = AdminDataTransfer.GetStatus(paths),
        };
    }

    /// <summary>读出一门课当前的章节号与题号（拉取前后各读一次，用于"新增/消失"对比）。</summary>
    private static (HashSet<int> Chapters, HashSet<int> Subjects) ReadCourseIds(string dbPath, int courseId)
    {
        var chapters = new HashSet<int>();
        var subjects = new HashSet<int>();
        if (!File.Exists(dbPath)) return (chapters, subjects);
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 30,
            }.ToString());
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select ichapterid from coursechapter where icourseid=@c";
                cmd.Parameters.AddWithValue("@c", courseId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) chapters.Add(reader.GetInt32(0));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select isubjectid from coursesubject where icourseid=@c";
                cmd.Parameters.AddWithValue("@c", courseId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) subjects.Add(reader.GetInt32(0));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 读取课程题号失败（对比报告将为空）: {ex.Message}");
        }
        return (chapters, subjects);
    }

    /// <summary>
    /// 这门课在主库里有没有正文（= 导出产物里包含它）。读不出来时按"有"处理，保持原行为。
    ///
    /// ⚠️ 必须写 <c>main.coursesubject</c>：这个连接是从**连接池**里拿的，池里同一连接串的连接
    /// 可能已经被 <see cref="OpenBank"/> 用过（那个方法会 ATTACH 拉取库并建同名的 TEMP VIEW 遮蔽表）。
    /// 不带库名时 `coursesubject` 会命中那个视图（= 主库 ∪ 拉取库），于是"只有拉取库里有的课程"
    /// 也被判成"主库有正文" —— 拉取会把内容写进主库、编辑的备份也会备份错库（实测：
    /// pulled 库里的课程被备份成主库，备份里根本没有那道题）。
    /// </summary>
    internal static bool MainHoldsCourseContent(string mainDbPath, int courseId)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = mainDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 30,
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select count(*) from main.coursesubject where icourseid=@c";
            cmd.Parameters.AddWithValue("@c", courseId);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }
        catch
        {
            return true;
        }
    }

    private static string ReadCourseName(string dbPath, int courseId)
    {
        try
        {
            using var cn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(dbPath),
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT ccoursename FROM course WHERE icourseid = $c";
            cmd.Parameters.AddWithValue("$c", courseId);
            return cmd.ExecuteScalar() as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
