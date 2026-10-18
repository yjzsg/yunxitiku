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

internal static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {

        // 端点搬出 Program.cs 后拿不到顶层局部变量；AppPaths 是单例，按需取同一个实例。
        var paths = app.Services.GetRequiredService<AppPaths>();
    app.MapPost("/api/admin/user-action", async (HttpRequest request, string? user, AuthStore auth, SessionStore sessionStore, UserDataStore userData) =>
    {
        var admin = CleanUserName(user);
        if (!admin.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        }
        var body = await ReadJsonBody(request);
        var target = CleanUserName(GetBodyString(body, "target"));
        var action = GetBodyString(body, "action").Trim().ToLowerInvariant();
        if (target.Length == 0 || target.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { ok = false, error = "不能操作管理员账号" }, statusCode: 400);
        }
        // 保留名（sessions.json 是登录会话存储，不是用户）：建/删都会破坏会话文件
        if (IsReservedUserName(target))
        {
            return Results.Json(new { ok = false, error = $"「{target}」是系统保留名称，不能作为账号" }, statusCode: 400);
        }

        Directory.CreateDirectory(paths.UserDataRoot);
        var profile = auth.Get(target);
        if (action == "disable")
        {
            profile ??= auth.CreateProfile(paths.DefaultLoginPassword);
            profile["disabled"] = true;
            profile["disabledAt"] = NowText();
            // 停用也换凭据版本：将来重新启用时，停用前签发的会话不会跟着"复活"
            AuthStore.RotateCredentialVersion(profile);
            auth.Set(target, profile);
            auth.Save();
            var revokeOnDisable = sessionStore.RevokeUser(target);
            return Results.Json(new { ok = true, user = target, disabled = true, sessionRevoke = SessionRevokeReport(revokeOnDisable) });
        }
        if (action == "enable")
        {
            if (profile is not null)
            {
                profile["disabled"] = false;
                profile["enabledAt"] = NowText();
                auth.Set(target, profile);
                auth.Save();
            }
            return Results.Json(new { ok = true, user = target, disabled = false });
        }
        if (action == "create")
        {
            if (profile is not null || File.Exists(UserDataPath(paths, target)))
            {
                return Results.Json(new { ok = false, error = "账号已存在" }, statusCode: 409);
            }
            profile = auth.CreateProfile(paths.DefaultLoginPassword);
            profile["mustChangePassword"] = true;
            profile["createdAt"] = NowText();
            // 新账号给一个新凭据版本：重名的旧账号（已删除）留下的会话记录不能因此生效
            AuthStore.RotateCredentialVersion(profile);
            auth.Set(target, profile);
            auth.Save();
            // 空档案也走同一把用户锁，保持"这个用户的数据文件只由持锁者写"这一不变式
            userData.Clear(target);
            return Results.Json(new { ok = true, user = target, created = true, defaultPassword = paths.DefaultLoginPassword });
        }
        if (action == "reset-password")
        {
            var fresh = auth.CreateProfile(paths.DefaultLoginPassword);
            // 重置口令只该换凭据：把「题库分配」这类设置保留下来，
            // 否则管理员一按重置，这个用户就被改成「可见全部题库」了。
            // （credentialVersion 必须排除：它是凭据的一部分，下面显式轮换）
            if (profile is not null)
            {
                foreach (var pair in profile)
                {
                    if (pair.Key is "salt" or "hash" or "hashAlgo" or "changedAt" or AuthStore.CredentialVersionKey) continue;
                    fresh[pair.Key] = pair.Value;
                }
            }
            fresh["mustChangePassword"] = true;
            fresh["resetAt"] = NowText();
            // 重置 = 换凭据：旧令牌的凭据版本必须对不上（sessions.json 写失败也不会复活）
            AuthStore.RotateCredentialVersion(fresh);
            auth.Set(target, fresh);
            auth.Save();
            // 重置口令必须吊销既有会话，否则旧 cookie 还能继续用
            var revokeOnReset = sessionStore.RevokeUser(target);
            return Results.Json(new { ok = true, user = target, resetPassword = true, defaultPassword = paths.DefaultLoginPassword, sessionRevoke = SessionRevokeReport(revokeOnReset) });
        }
        if (action == "clear-data")
        {
            // 走 UserDataStore 的按用户锁：否则会与用户正在进行的 save 交错
            // （用户读到旧内容 → 管理员清空 → 用户保存，清空被整体覆盖）
            userData.Clear(target);
            return Results.Json(new { ok = true, user = target, cleared = true });
        }
        if (action == "delete")
        {
            // 删除用户数据必须在用户的锁内：否则用户的 save 可能在删除之后落地，
            // 把已删账号的数据文件"复活"，而 auth 里已经没有这个账号了。
            userData.Delete(target);
            auth.Remove(target);
            auth.Save();
            // 删号后会话立即失效（否则旧 cookie 还能读写，甚至把数据文件"复活"）；
            // 会话文件写失败时旧记录会留在磁盘上，但重启后 middleware 按"账号不存在"继续拒
            var revokeOnDelete = sessionStore.RevokeUser(target);
            return Results.Json(new { ok = true, user = target, deleted = true, sessionRevoke = SessionRevokeReport(revokeOnDelete) });
        }
        return Results.Json(new { ok = false, error = "unknown action" }, statusCode: 400);
    });

    // 按用户分配题库：读（assigned=null 表示"全部可见"）/ 写（courses=null 恢复全部）
    app.MapGet("/api/admin/user-courses", (HttpContext context, AuthStore auth, string? user, string? target) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        var name = CleanUserName(target);
        if (name.Length == 0) return Results.Json(new { ok = false, error = "缺少用户名" }, statusCode: 400);
        var allowed = UserCourses.AllowedIds(auth, name);
        return Results.Json(new { ok = true, user = name, assigned = allowed is null ? null : allowed.OrderBy(id => id).ToList() });
    });

    app.MapPost("/api/admin/user-courses", async (HttpRequest request, HttpContext context, AuthStore auth, string? user) =>
    {
        /* 用**会话身份**判管理员。`?user=` 是客户端可控的，只看它就等于伪鉴权
           （任何已登录用户传 `?user=admin` 就能冒充）；而且旧前端漏传参数时会误报 403。 */
        if (!IsAdminRequest(context))
        {
            return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        }
        try
        {
            var body = await ReadJsonBody(request);
            var target = CleanUserName(GetBodyString(body, "user"));
            if (target.Length == 0) return Results.Json(new { ok = false, error = "缺少用户名" }, statusCode: 400);
            if (IsReservedUserName(target))
            {
                return Results.Json(new { ok = false, error = $"「{target}」是系统保留名称，不能作为账号" }, statusCode: 400);
            }
            List<int>? ids = null;
            if (body.TryGetValue("courses", out var value) && value is not null)
            {
                ids = new List<int>();
                if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n)) ids.Add(n);
                        else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var s)) ids.Add(s);
                    }
                }
            }
            UserCourses.Save(auth, target, ids);
            return Results.Json(new { ok = true, user = target, assigned = ids is null ? null : ids.OrderBy(id => id).ToList() });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });

    // 检查这些课程在上游有没有更新（按增量水位各探一次章节与题目；不做整表探测）
    app.MapPost("/api/admin/course-update-check", async (HttpRequest request, QuestionBank bank, IConfiguration configuration, string? user) =>
    {
        if (!IsAdminRequest(request)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        try
        {
            var body = await ReadJsonBody(request);
            var ids = new List<int>();
            if (body.TryGetValue("courseIds", out var value) && value is JsonElement element && element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n) && n > 0) ids.Add(n);
                }
            }
            ids = ids.Distinct().Take(200).ToList();
            var checks = await CourseUpdateChecker.CheckAsync(bank, ids, configuration);
            return Results.Json(new { ok = true, results = CourseUpdateChecker.Describe(ids, checks) });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });

    app.MapPost("/api/admin/update-bank", async (HttpRequest request, HttpContext context, string? user, int? courseId, bool? dryRun, bool? force,
        QuestionBank bank, IConfiguration configuration, ChapterMapStore chapterMap) =>
    {
        if (!IsAdminRequest(context))
        {
            return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        }
        if (!courseId.HasValue || courseId.Value <= 0)
        {
            return Results.Json(new { ok = false, error = "请选择要更新的题库" }, statusCode: 400);
        }
        if (!request.HasFormContentType)
        {
            // The admin page probes this endpoint with ?dryRun=true to decide whether the deployment
            // pulls or uploads. Answering "pull" is what turns the button into 拉取更新.
            if (dryRun == true)
            {
                return Results.Json(new
                {
                    ok = true,
                    mode = "pull",
                    dryRun = true,
                    message = "Docker 版已启用直连拉取；dryRun 未执行实际更新。",
                });
            }
            /* 同一门课重复发起：进度记录按课程 ID 只有一条，两个并发拉取只会互相覆盖对方的状态，
               写入侧本来也要排队等闸（见 BankWriteGate）。这里直接拒绝，让用户等前一次结束。
               放在上游"有没有更新"检查之前：重复点击不该再打一次外网、也不该白做一遍备份。 */
            if (Puller.PullProgressTracker.Shared.IsActive(courseId.Value))
            {
                return Results.Json(new
                {
                    ok = false,
                    mode = "pull",
                    courseId = courseId.Value,
                    error = Puller.PullProgressTracker.Shared.DescribeBusy(courseId.Value),
                }, statusCode: 409);
            }
            /* 拉取前先问一次上游「有没有更新」：**确定没有**才跳过，省掉一次几分钟的白拉。
               「拿不到结论」（网络超时 / SOAP 错误 / 上游换了格式）**不阻断拉取** —— 宁可多拉一次，
               也不能因为检查本身出错就彻底拉不了（这也是 HasUpdateAsync 注释里的原意）。
               要强制拉取可以带 force=true。 */
            if (force != true)
            {
                try
                {
                    if (!await CourseUpdateChecker.HasUpdateAsync(bank, courseId.Value, configuration))
                    {
                        return Results.Json(new
                        {
                            ok = true,
                            skipped = true,
                            state = "unchanged",
                            courseId = courseId.Value,
                            message = "该题库已是最新，未执行拉取。",
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [update-bank] 拉取前检查失败，改为直接拉取: {SafeError(ex)}");
                }
            }
            try
            {
                SqliteConnection.ClearAllPools();
                var result = ServerBankPuller.PullCourseIntoBank(paths, app.Configuration, courseId.Value);
                // 题库换过之后章节归属要重算 —— 映射是外挂文件，不会跟着题库走。
                chapterMap.TryRebuildQuietly("拉取更新");
                return Results.Json(result);
            }
            catch (Puller.PullProgressTracker.PullAlreadyRunningException ex)
            {
                // 上面那次预检与这里之间有极小窗口（两个请求同时进来），到登记处才发现重复：
                // 同一个 409，别降级成 500。
                return Results.Json(new
                {
                    ok = false,
                    mode = "pull",
                    courseId = courseId.Value,
                    error = ex.Message,
                }, statusCode: 409);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, mode = "pull", error = SafeError(ex) }, statusCode: 500);
            }
        }
        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length <= 0)
        {
            return Results.Json(new { ok = false, error = "请选择要上传的题库 zip" }, statusCode: 400);
        }
        var originalName = Path.GetFileName(file.FileName);
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        var tempFile = Path.Combine(Path.GetTempPath(), $"yunxi-course-upload-{Guid.NewGuid():N}{ext}");
        try
        {
            await using (var output = File.Create(tempFile))
            {
                await file.CopyToAsync(output);
            }
            var result = AdminDataTransfer.ImportCourseBank(paths, tempFile, originalName, courseId.Value);
            chapterMap.TryRebuildQuietly("题库整包上传");
            return Results.Json(result);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    });

    // 拉取进度：POST /api/admin/update-bank 仍是同步返回最终结果，管理页在它进行期间轮询这里。
    //   · 不带 courseId：返回**全部**条目 { ok, snapshots }，管理页刷新后据此接上所有在跑的拉取；
    //   · 带 courseId：保持旧的单条形状 { ok, known, snapshot }（老前端还在用，别改）。
    // 没有记录（没拉过 / 进程重启过 / 已过期）时 snapshot 为 null + active=false，前端据此降级。
    app.MapGet("/api/admin/update-bank/progress", (HttpContext context, string? user, int? courseId) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        if (!courseId.HasValue || courseId.Value <= 0)
        {
            // 已结束但还在保留期内的记录也会返回；调用方自己按 active 过滤
            return Results.Json(new { ok = true, snapshots = Puller.PullProgressTracker.Shared.ReadAll() });
        }
        var snapshot = Puller.PullProgressTracker.Shared.Read(courseId.Value);
        return Results.Json(snapshot is null
            ? new { ok = true, courseId = courseId.Value, active = false, known = false }
            : new { ok = true, known = true, snapshot });
    });

    app.MapPost("/api/admin/clean-ads", (HttpContext context, string? user, int? courseId) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        try
        {
            using var gate = BankWriteGate.Enter();
            SqliteConnection.ClearAllPools();
            var dataRoot = Directory.GetParent(paths.SqlitePath)?.FullName ?? paths.BaseRoot;
            var backupRoot = Path.Combine(dataRoot, "_backups");
            var backupDir = Path.Combine(backupRoot, "clean-ads-" + TimestampUnique());
            var result = Puller.AdCleaner.CleanDatabase(paths.SqlitePath, courseId ?? 0, backupDir);
            // 未购课程的正文在拉取库，也要一起清
            var pulled = File.Exists(paths.PulledSqlitePath)
                ? Puller.AdCleaner.CleanDatabase(paths.PulledSqlitePath, courseId ?? 0,
                    Path.Combine(backupRoot, "clean-ads-pulled-" + TimestampUnique()))
                : null;
            BackupRetention.Prune(backupRoot);
            var courses = result.Courses + (pulled?.Courses ?? 0);
            var scanned = result.SubjectsScanned + (pulled?.SubjectsScanned ?? 0);
            var cleanedSubjects = result.SubjectsCleaned + (pulled?.SubjectsCleaned ?? 0);
            var adHits = result.AdHits + (pulled?.AdHits ?? 0);
            var charsRemoved = result.CharsRemoved + (pulled?.CharsRemoved ?? 0);
            return Results.Json(new
            {
                ok = true,
                mode = "clean-ads",
                courses,
                subjectsScanned = scanned,
                subjectsCleaned = cleanedSubjects,
                adHits,
                charsRemoved,
                backup = result.BackupPath,
                backupPulled = pulled?.BackupPath,
                message = adHits == 0
                    ? "未发现需要清洗的广告内容"
                    : $"已清洗 {cleanedSubjects} 道题中的 {adHits} 处广告（涉及 {courses} 门课程）",
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });

    app.MapGet("/api/admin/data/status", (HttpContext context, string? user) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        return Results.Json(AdminDataTransfer.GetStatus(paths));
    });

    app.MapGet("/api/admin/data/download", (HttpContext context, string? user, string? type) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        try
        {
            var kind = NormalizeDataType(type);
            var tempZip = kind == "bank"
                ? AdminDataTransfer.CreateBankZip(paths)
                : AdminDataTransfer.CreateUserDataZip(paths);
            var fileName = kind == "bank"
                ? $"yunxi-question-bank-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
                : $"yunxi-userdata-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            var stream = File.OpenRead(tempZip);
            context.Response.OnCompleted(() =>
            {
                stream.Dispose();
                TryDeleteFile(tempZip);
                return Task.CompletedTask;
            });
            return Results.File(stream, "application/zip", fileName);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });

    app.MapPost("/api/admin/data/upload", async (HttpRequest request, string? user, string? type, AuthStore auth, UserDataStore userData, ChapterMapStore chapterMap) =>
    {
        if (!IsAdminRequest(request)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        if (!request.HasFormContentType) return Results.Json(new { ok = false, error = "请使用表单上传文件" }, statusCode: 400);
        var kind = NormalizeDataType(type);
        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length <= 0) return Results.Json(new { ok = false, error = "请选择要上传的文件" }, statusCode: 400);

        var originalName = Path.GetFileName(file.FileName);
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        var tempFile = Path.Combine(Path.GetTempPath(), $"yunxi-upload-{Guid.NewGuid():N}{ext}");
        try
        {
            await using (var output = File.Create(tempFile))
            {
                await file.CopyToAsync(output);
            }
            var result = kind == "bank"
                ? AdminDataTransfer.ImportBank(paths, tempFile, originalName)
                : AdminDataTransfer.ImportUserData(paths, auth, userData, tempFile, originalName);
            if (kind == "bank") chapterMap.TryRebuildQuietly("题库整包上传");
            return Results.Json(result);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    });

    /* ── 备份列表 / 下载 / 回滚 ────────────────────────────────────────────────
       背景：「自动备份」这件事以前只做了一半 —— 换题库、导入用户数据、删题目这些
       破坏性动作都会在 `data/_backups` 或 `userdata/_backups` 里留一份，但界面上只有
       「N 个备份」这个数字，既看不到是什么时候的、也拿不回来。出事时用户只能看着数字
       干瞪眼（提交记录里用户原话：「自动备份」写着但无恢复入口 = 只是在制造自己的备份）。

       这里补上三段：列 → 下载（先自己存一份）→ 回滚。
       回滚**复用** ImportUserData / ImportBank 的提交逻辑，因此自动继承它们的
       「先把当前状态再备份一次 + 两阶段可撤销」。 */

    app.MapGet("/api/admin/backups", (HttpContext context, AppPaths paths) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        try
        {
            return Results.Json(new { ok = true, items = AdminDataTransfer.ListBackups(paths) });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });

    app.MapGet("/api/admin/backups/download", (HttpContext context, AppPaths paths, string name) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        var file = AdminDataTransfer.ResolveBackup(paths, name);
        if (file is null) return Results.Json(new { ok = false, error = "备份不存在或已过保留期清理" }, statusCode: 404);
        // 直接给文件流：备份可能有几百 MB，不能 ReadAllBytes 进内存。
        return Results.File(file.Value.Path, "application/octet-stream", file.Value.DownloadName);
    });

    app.MapPost("/api/admin/backups/restore", (HttpContext context, AppPaths paths, AuthStore auth, UserDataStore userData, ChapterMapStore chapterMap) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        try
        {
            var name = (context.Request.Query["name"].ToString() ?? "").Trim();
            if (name.Length == 0) return Results.Json(new { ok = false, error = "缺少 name 参数" }, statusCode: 400);
            var result = AdminDataTransfer.RestoreBackup(paths, auth, userData, name);
            // 题库换了，章节映射必须重建，否则学员端会拿旧映射配新库
            chapterMap.TryRebuildQuietly("回滚备份");
            return result;
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });

    app.MapGet("/api/admin/bank-editor/questions", (HttpContext context, QuestionBank bank, string? user, int courseId, int? chapterId, string? chapterIds, string? q, string? ids, int? limit) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        try
        {
            return Results.Json(bank.GetEditorQuestions(courseId, chapterId, ParseIdList(chapterIds), q ?? "", ParseIdList(ids), limit));
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });

    app.MapGet("/api/admin/bank-editor/question", (HttpContext context, QuestionBank bank, string? user, int id) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        try
        {
            var question = bank.GetEditorQuestion(id);
            return question is null ? Results.Json(new { ok = false, error = "question not found" }, statusCode: 404) : Results.Json(question);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });

    app.MapPost("/api/admin/bank-editor/question", async (HttpRequest request, QuestionBank bank, string? user) =>
    {
        if (!IsAdminRequest(request)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        try
        {
            var body = await ReadJsonBody(request);
            return Results.Json(bank.SaveEditorQuestion(body, user));
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });

    // 把本地改过的题还原成服务端版本（本地改动叠层里的记录随之删除）
    app.MapPost("/api/admin/bank-editor/restore", (HttpContext context, QuestionBank bank, string? user, int? id) =>
    {
        if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
        try
        {
            SqliteConnection.ClearAllPools();
            return Results.Json(bank.RestoreEditorQuestion(id ?? 0));
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = SafeError(ex) }, statusCode: 400);
        }
    });
    }
}
