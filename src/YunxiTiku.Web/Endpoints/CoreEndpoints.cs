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

internal static class CoreEndpoints
{
    public static void MapCore(this WebApplication app)
    {

        // 端点搬出 Program.cs 后拿不到顶层局部变量；AppPaths 是单例，按需取同一个实例。
        var paths = app.Services.GetRequiredService<AppPaths>();
    app.MapGet("/", (HttpContext context) =>
    {
        // 首页决定引用哪个 ?v=，必须每次回源校验（见上面 OnPrepareResponse 的注释）
        context.Response.Headers.CacheControl = "no-cache";
        return Results.File(Path.Combine(paths.PublicRoot, "index.html"), "text/html; charset=utf-8");
    });

    app.MapGet("/api/health", () =>
    {
        // 真跑一条读查询：库坏了要报 not-ok，而不是"文件存在就算健康"（否则监控全绿而服务全死）。
        // 也不再返回题库规模明细（未鉴权即可读，属信息泄露）。
        return Results.Json(new { ok = true, sqlite = QuestionBank.ProbeHealthy(paths.SqlitePath) });
    });

    // 账号清单只给管理员看（/api/admin/* 由中间件强制要求管理员会话）。
    // 登录页不再需要它——它靠 /api/auth/session 判断"上次登录的人还是不是有效会话"。
    app.MapGet("/api/admin/users", () =>
    {
        var users = AppHelpers.EnumerateUserFiles(paths)
            // 排除会话存储：它不是用户（否则界面会一直冒出一个叫 sessions 的账号）
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => CleanUserName(name!))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!users.Any(name => name.Equals("admin", StringComparison.OrdinalIgnoreCase))) users.Insert(0, "admin");
        return Results.Json(users.Select(name => new
        {
            name
        }));
    });
    }
}
