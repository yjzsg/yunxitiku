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

internal static class ContentEndpoints
{
    public static void MapContent(this WebApplication app)
    {

        // 端点搬出 Program.cs 后拿不到顶层局部变量；AppPaths 是单例，按需取同一个实例。
        var paths = app.Services.GetRequiredService<AppPaths>();
    // ── 章节映射（外挂，见 ChapterMapStore 的注释） ─────────────────────────────
    // 整卷类卷册（历年真题/模考/预测…）的题在题库里没有章节归属，靠外挂映射补。
    // 映射落 data/chapter-map.json，不写进 question-bank.db（整包上传会替换那个文件）。
    /* 这里原有 /api/admin/chapter-map/status 与 /rebuild 两个端点，前端从未调用过：
       章节映射的重建由启动自检、换题库、保存题目三处内部触发，管理员没有手动重建的需要。
       已删除（保留 ChapterMapStore 内部实现）。 */

    app.MapGet("/api/chapter-map", (ChapterMapStore chapterMap) =>
    {
        var map = chapterMap.Load();
        return Results.Json(new { ok = true, generatedAt = map.GeneratedAt, chapters = map.Chapters, map = map.Map });
    });

    // 前台题库读取一律按"该用户被分配的题库"过滤（admin / 未分配 = 全部可见）
    app.MapGet("/api/courses", async (HttpContext context, QuestionBank bank, AuthStore auth, IConfiguration configuration, string? q, string? available) =>
    {
        var availableOnly = IsTruthy(available);
        // 管理端列表（不带 available=1）额外带上「上游有、本地没有」的课程，让管理员看到全部并可拉取。
        // 上游目录不可用（没配账号 / 网络抖动）时静默降级为只列本地课程 —— 绝不能因此打不开列表。
        var upstream = availableOnly ? null : await TryLoadUpstreamCatalogAsync(configuration);
        return Results.Json(bank.GetCourses(q ?? "", availableOnly, UserCourses.AllowedIds(auth, SessionUser(context)),
            AppHelpers.ExcludedCourseClasses(configuration), upstream));
    });

    app.MapGet("/api/chapters", (HttpContext context, QuestionBank bank, AuthStore auth, int courseId) =>
        UserCourses.IsAllowed(auth, SessionUser(context), courseId)
            ? Results.Json(bank.GetChapters(courseId))
            : Results.Json(new { ok = false, error = "未分配该题库" }, statusCode: 403));

    app.MapGet("/api/types", (HttpContext context, QuestionBank bank, AuthStore auth, int courseId, int? chapterId, string? chapterIds) =>
        UserCourses.IsAllowed(auth, SessionUser(context), courseId)
            ? Results.Json(bank.GetTypes(courseId, chapterId, ParseIdList(chapterIds)))
            : Results.Json(new { ok = false, error = "未分配该题库" }, statusCode: 403));

    app.MapGet("/api/questions", (HttpContext context, QuestionBank bank, AuthStore auth, int courseId, int? chapterId, string? chapterIds, int? typeId, string? q, string? order, int? limit, string? ids) =>
        UserCourses.IsAllowed(auth, SessionUser(context), courseId)
            ? Results.Json(bank.GetQuestions(courseId, chapterId, ParseIdList(chapterIds), typeId, q ?? "", order ?? "", limit, ParseIdList(ids)))
            : Results.Json(new { ok = false, error = "未分配该题库" }, statusCode: 403));

    /* 只回「题目 id → 章节 id」的扁平数组，给前端补章节树的「已做」统计用。
       为什么不复用 /api/questions：那个接口每道题都带题干（还要解密），
       一门课几千题就是 2MB+；这里只有两个整数，小一个数量级。
       格式：[qid1, cid1, qid2, cid2, ...] —— 比对象数组再小一半。 */

    app.MapGet("/api/question-chapters", (HttpContext context, QuestionBank bank, AuthStore auth, int courseId) =>
        UserCourses.IsAllowed(auth, SessionUser(context), courseId)
            ? Results.Json(bank.GetQuestionChapterPairs(courseId))
            : Results.Json(new { ok = false, error = "未分配该题库" }, statusCode: 403));

    app.MapGet("/api/question", (HttpContext context, QuestionBank bank, AuthStore auth, int id) =>
    {
        var question = bank.GetQuestion(id);
        if (question is null) return Results.Json(new { error = "question not found" }, statusCode: 404);
        return UserCourses.IsAllowed(auth, SessionUser(context), bank.CourseIdOfSubject(id))
            ? Results.Json(question)
            : Results.Json(new { ok = false, error = "未分配该题库" }, statusCode: 403);
    });

    app.MapGet("/assets/{**relative}", (HttpContext context, string relative) =>
    {
        relative = Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);
        // 先查主库图片目录，再查拉取库图片目录（未购课程的题图落在 assets-pulled/ 下）。
        foreach (var rootPath in new[] { paths.DataAssetsRoot, paths.PulledAssetsRoot })
        {
            var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Results.Json(new { error = "bad path" }, statusCode: 400);
            if (File.Exists(path))
            {
                // 资产是同源提供的：SVG 等可执行内容直接顶层打开会执行脚本（绕过前端 sanitizer）。
                // 用 CSP sandbox 锁死，并禁止嗅探。
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                // 题图 URL 不带内容哈希，不能长缓存：改了图 URL 不变，长缓存会一直发旧图。
                // 用 no-cache 而不是 no-store：Results.File 会带 Last-Modified（实测不带 ETag），
                // 浏览器每次回源校验，文件没变就 304，既不做启发式缓存也不重发整张图。
                context.Response.Headers.CacheControl = "no-cache";
                return Results.File(path, ContentType(path));
            }
        }
        return Results.Json(new { error = "asset not found" }, statusCode: 404);
    });
    }
}
