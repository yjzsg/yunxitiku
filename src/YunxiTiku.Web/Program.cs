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

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
SQLitePCL.Batteries.Init();

if (args.Length >= 2 && args[0] == "--healthcheck")
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        var text = await client.GetStringAsync(args[1]);
        return text.Contains("\"sqlite\":true", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_URLS")))
{
    builder.WebHost.UseUrls(builder.Configuration["App:Urls"] ?? "http://0.0.0.0:8787");
}
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = 128 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024;
    /* 请求行（METHOD + URI + 版本）上限，Kestrel 默认只有 8192 字节，超了直接 HTTP 414。
       错题本会把全部错题 id 拼进查询串（/api/questions?...&ids=a,b,c,...），实测 800 条约
       8188 字节还过得去，850 条 8738 字节就已经 414 —— 阈值正好卡在默认值上。
       这是**纵深防御**，不能替代前端分批：前端已改为分批请求，这里放宽只是让「一批稍大一点」
       不至于被 Kestrel 在到达路由前就掐掉。ids 再翻几倍，32KB 同样会被顶穿，所以两边都得有。
       另外：若部署在 Nginx/Apache/IIS/Cloudflare 之后，那些组件的请求行（URL）上限通常比这里
       更小，会在到达 Kestrel 之前就返回 414 —— 只改这里挡不住，反代侧也得同步放宽。 */
    options.Limits.MaxRequestLineSize = 32 * 1024;
});
builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<AuthStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<UserDataStore>();
builder.Services.AddSingleton<QuestionBank>();
builder.Services.AddSingleton<ChapterMapStore>();
/* 每日推送提醒（见文件末尾 PushConfigStore / PushScheduler）。
   注册成单例再挂 HostedService，是为了让 /api/push/test 和 /api/push/run 能拿到**同一个**
   调度器实例（AddHostedService 默认建的是独立实例，端点里注入不到它）。 */
builder.Services.AddSingleton<PushConfigStore>();
/* Web Push（浏览器通知）：VAPID 密钥全站一份、存在 userdata/push/_server/ 下；
   WebPushSender 要拿 PushConfigStore 才能把 404/410 的死订阅顺手删掉。 */
builder.Services.AddSingleton<VapidKeyStore>();
builder.Services.AddSingleton<WebPushSender>();
builder.Services.AddSingleton<PushScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PushScheduler>());
/* 反代/隧道部署时按真实客户端 IP 计数（否则限流把所有人算成一个 IP）。
   ⚠️ 默认**只信任回环**，与 docker-compose.yml 默认只把 8787 绑在 127.0.0.1 一致。
   旧配置把 KnownProxies/KnownNetworks 清空 = 信任任何来源的转发头：谁能直连到应用端口，
   谁就能用伪造的 X-Forwarded-For 绕过登录限流、污染日志与安全诊断。
   放在反代/隧道后面时必须把代理地址写进 App:TrustedProxies（逗号分隔，IP 或 CIDR）：
   Docker 里从宿主机/其它容器过来的连接一般显示为网桥网关（如 172.17.0.1），
   不确定就在容器里 `ip route` 看默认网关。做法见 README「安全说明 / 部署前必读」。 */
var trustedProxies = (builder.Configuration["App:TrustedProxies"] ?? "")
    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var entry in trustedProxies)
    {
        // 全限定类型名：System.Net.IPNetwork 与 AspNetCore 的同名类型在本文件里都可见
        if (Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse(entry, out var cidr))
        {
            options.KnownNetworks.Add(cidr);
        }
        else if (IPAddress.TryParse(entry, out var proxyIp))
        {
            options.KnownProxies.Add(proxyIp);
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [WARN] App:TrustedProxies 里的「{entry}」不是合法 IP/CIDR，已忽略");
        }
    }
    if (trustedProxies.Length == 0)
    {
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Loopback, 8));       // 127.0.0.0/8
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.IPv6Loopback, 128)); // ::1/128
    }
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

/* 响应压缩：学习看板 / 深度分析会拉全库题目（单科目约 3.8MB JSON）。
   经 Cloudflare Tunnel 那条链路很慢，不压缩时前端长时间转圈、看起来像卡死。
   开 Brotli/Gzip 后同一个响应大约降到 1/6。 */
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;   // 经反向代理时后端看到的未必是 https
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});
/* brotli 用 Optimal：Fastest 下它反而比 gzip 大（实测 915KB vs 720KB），
   而浏览器优先选 brotli，等于让用户拿到最差的那个。 */
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

/* 请求体解压：保存走的是整包上传（线上实测 1,006,794 字节），
   国内移动网络下这一趟要好几秒。客户端用 CompressionStream('gzip') 压到约 1/10 再发，
   这里负责还原 —— 端点读到的仍是原始 JSON，revision 仍按原文算，契约不变。
   老客户端不带 Content-Encoding，行为完全不变。
   ⚠️ RequestDecompressionOptions 没有 SizeLimit（.NET 这里不提供），解压炸弹靠
   /api/user/save 里改成「限长读取」来兜（见 ReadBodyWithLimitAsync）。 */
builder.Services.AddRequestDecompression();

var app = builder.Build();

app.UseResponseCompression();   // 必须尽早：要压在静态文件与路由之前
app.UseRequestDecompression();  // 同理：必须在端点读 body 之前

// 全局异常处理：任何未捕获异常都回 JSON，而不是 Production 下的裸 500 空响应体。
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = error switch
    {
        UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, error.Message),
        InvalidOperationException => (StatusCodes.Status400BadRequest, error.Message),
        JsonException => (StatusCodes.Status400BadRequest, "请求体不是有效 JSON"),
        _ => (StatusCodes.Status500InternalServerError, "服务器内部错误，请查看服务端日志"),
    };
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsJsonAsync(new { ok = false, error = message });
}));
app.UseForwardedHeaders();

var paths = app.Services.GetRequiredService<AppPaths>();
var sessions = app.Services.GetRequiredService<SessionStore>();
Directory.CreateDirectory(paths.UserDataRoot);
Directory.CreateDirectory(paths.DataAssetsRoot);
Directory.CreateDirectory(Path.GetDirectoryName(paths.SqlitePath) ?? paths.BaseRoot);
QuestionBank.EnsureDatabase(paths.SqlitePath);
/* 上次发布（整包上传 / 单课导入）如果死在切换途中，启动时收尾：DB 与 assets 必须
   回到同一版本（见 BankPublish）。放着不管的话线上可能是"新库 + 半套旧图"。 */
BankPublish.RecoverPending(paths);

// 章节映射是外挂文件（data/chapter-map.json），不跟着题库走。
// 首次部署 / 映射文件丢了 / 题库换过，都在后台补一次，不用手动点重建。
app.Services.GetRequiredService<ChapterMapStore>().WarmUpInBackground();

if (app.Configuration.GetValue<bool>("App:EnableHttpsRedirect"))
{
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    try
    {
        await next();
    }
    finally
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // 查询串里带用户名，日志里打码（user=xxx -> user=***）
        var query = Regex.Replace(context.Request.QueryString.Value ?? "", @"(?i)(user|password|token)=[^&]*", "$1=***");
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context.Request.Method} {context.Request.Path}{query} {context.Response.StatusCode} {elapsed:0.0}ms");
    }
});

app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    if (!headers.ContainsKey("X-Content-Type-Options")) headers["X-Content-Type-Options"] = "nosniff";
    if (!headers.ContainsKey("X-Frame-Options")) headers["X-Frame-Options"] = "SAMEORIGIN";
    if (!headers.ContainsKey("Referrer-Policy")) headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // 业务响应不该被中间代理缓存
    if (context.Request.Path.StartsWithSegments("/api") && !headers.ContainsKey("Cache-Control"))
    {
        headers["Cache-Control"] = "no-store";
    }
    await next();
});
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    // 前导多斜杠路径（//api/...）用 StartsWithSegments("/api") 判不出来，会掉进 SPA 回退
    // 返回 200 + HTML，被扫描器误判成"端点还在"。这里直接判 404 JSON。
    // 只拦**前导**双斜杠：路径中段的双斜杠可能是合法的资产引用，不误伤。
    var rawPath = path.Value ?? "";
    if (rawPath.StartsWith("//", StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "接口不存在" });
        return;
    }
    // 状态变更类 /api 请求做跨站校验：SameSite 按 site 判定，同主机不同端口算"同站"，
    // 所以同 NAS 上其它服务的页面能带上本应用的 cookie 发起 CSRF。
    // 浏览器会带 Sec-Fetch-Site；非浏览器客户端（curl 等）不带，放行。
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
        && path.StartsWithSegments("/api"))
    {
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString().Trim();
        if (fetchSite.Length > 0 && !fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
            && !fetchSite.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "跨站请求被拒绝" });
            return;
        }
    }
    // 未登录也能访问的只有健康检查、登录、以及「我是谁」——用户名列表不再是公开信息
    var protectedApi = path.StartsWithSegments("/api")
        && !path.StartsWithSegments("/api/health")
        && !path.StartsWithSegments("/api/auth/session")
        && !path.StartsWithSegments("/api/auth/login");
    var protectedAsset = path.StartsWithSegments("/assets");
    if (!protectedApi && !protectedAsset)
    {
        await next();
        return;
    }

    var sessionUser = sessions.Resolve(context.Request);
    if (sessionUser.Length == 0)
    {
        // 诊断用：把"浏览器到底有没有把凭据带上来"记下来。手机端「登录已失效」反复发生时，
        // 这一行能一眼区分「凭据没送到（浏览器/代理吞 cookie）」与「送到了但服务端不认」。
        var (hasCookie, hasHeader) = sessions.CredentialPresence(context.Request);
        Console.WriteLine($"[session-miss] {context.Request.Path} cookie={hasCookie} header={hasHeader} " +
                          $"origin={context.Request.Headers["Origin"]} referer={context.Request.Headers["Referer"]}");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "登录已失效，请重新登录" });
        return;
    }
    var auth = context.RequestServices.GetRequiredService<AuthStore>();
    var profile = auth.Get(sessionUser);
    if (auth.IsDisabled(profile))
    {
        sessions.Remove(context.Request);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "账号已停用" });
        return;
    }
    // 账号已被删除（admin 无档案也合法）：会话立即作废，不必等 7 天过期。
    if (profile is null && !sessionUser.Equals("admin", StringComparison.OrdinalIgnoreCase))
    {
        sessions.SignOut(context);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "账号不存在，请重新登录" });
        return;
    }
    if (path.StartsWithSegments("/api/admin") && !sessionUser.Equals("admin", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "admin only" });
        return;
    }
    // 首次登录必须改密：服务端强制。以前只在前端拦，直接 curl 能绕过。
    // admin 在 accounts.dat 里可能**没有档案**（还在用初始口令）——那种情况同样必须改密。
    var mustChange = profile is not null
        ? auth.MustChangePassword(profile)
        : sessionUser.Equals("admin", StringComparison.OrdinalIgnoreCase);
    if (mustChange
        && !path.StartsWithSegments("/api/auth/session")
        && !path.StartsWithSegments("/api/auth/change-password")
        && !path.StartsWithSegments("/api/auth/logout"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "首次登录请先修改密码", code = "must_change_password" });
        return;
    }
    context.Items[SessionStore.UserItemKey] = sessionUser;
    await next();
});

var publicFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(paths.PublicRoot);
app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = publicFiles,
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = publicFiles,
    OnPrepareResponse = ctx =>
    {
        /* 缓存策略必须显式给，不能一个 Cache-Control 都不发。
           以前所有静态资源只带 Last-Modified，浏览器就按「启发式缓存」自己算新鲜期
           （Chrome 取 Last-Modified 到现在的 10%）。index.html 越老窗口越长 ——
           结果新版本部署几小时后，浏览器还在直接用旧 HTML，于是引用旧的 ?v=，
           CSS/JS 全是旧的，用户看到的就是「改了没生效」，只能手动强刷。

           规则：
             · HTML 和 sw.js 一律 no-cache（每次回源校验，命中 304 也很快）
             · 带 ?v= 的资源可以长缓存 —— URL 本身就是版本号，发新版就是新 URL
             · 其余（favicon.svg / manifest.json 这类没有版本号的）也走 no-cache，
               否则改了发不出去 */
        var headers = ctx.Context.Response.Headers;
        var name = ctx.File.Name;
        var versioned = ctx.Context.Request.Query.ContainsKey("v");
        if (versioned && !name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("sw.js", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
        else
        {
            headers.CacheControl = "no-cache";
        }
    },
});

app.MapCore();

app.MapAuth();

app.MapUserData();

app.MapAdmin();

app.MapContent();

/* ── 每日推送提醒 ────────────────────────────────────────────────────────
   配置存 userdata/push/<user>.json（**子目录**）。为什么不写进用户数据整包、为什么
   不能放顶层，见文件末尾 PushConfig 的注释。
   四个端点都走 ResolveRequestedUser：非管理员拿到的永远是自己，管理员可用 ?user= 指定。 */
app.MapPush();

// 未知的 /api/* 要回 404 JSON，而不是掉进下面的 SPA 回退：
// 回退会给出 200 + HTML，容易被误判成「端点还在」。
app.MapFallback("/api/{**rest}", () => Results.Json(new { ok = false, error = "接口不存在" }, statusCode: 404));
app.MapFallback((HttpContext context) =>
{
    // SPA 回退同样发 index.html，缓存策略必须和 MapGet("/") 一致（见上面 OnPrepareResponse 的注释）。
    // 不显式发的话这里一个 Cache-Control 都没有，浏览器按启发式缓存 index.html，
    // 新版本部署后用户还在用旧 HTML，于是引用旧的 ?v=。
    // HEAD / 也落到这里（MapGet("/") 不匹配 HEAD 方法），所以首页的 HEAD 同样靠这一处。
    context.Response.Headers.CacheControl = "no-cache";
    return Results.File(Path.Combine(paths.PublicRoot, "index.html"), "text/html; charset=utf-8");
});

app.Run();
return 0;
