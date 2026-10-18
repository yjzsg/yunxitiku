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

// 当前会话身份。登录页用它恢复「上次登录」，所以不带会话也能调；
// 但它只回答"这张 cookie 是谁"，不泄露任何账号清单。
app.MapGet("/api/auth/session", (HttpContext context, AuthStore auth, SessionStore sessionStore) =>
{
    var user = sessionStore.Resolve(context.Request);
    if (user.Length == 0) return Results.Json(new { ok = false, user = "" });
    if (auth.IsDisabled(user))
    {
        sessionStore.SignOut(context);
        return Results.Json(new { ok = false, user = "", error = "账号已停用，请联系管理员" });
    }
    var profile = auth.Get(user);
    // 账号已被删除（admin 没有档案也合法）：会话作废
    if (profile is null && !user.Equals("admin", StringComparison.OrdinalIgnoreCase))
    {
        sessionStore.SignOut(context);
        return Results.Json(new { ok = false, user = "", error = "账号不存在，请联系管理员" });
    }
    return Results.Json(new
    {
        ok = true,
        user,
        // 还是初始口令的账号：回登录页走改密流程，别让刷新绕过强制改密
        mustChangePassword = profile is null || auth.MustChangePassword(profile),
    });
});

app.MapPost("/api/auth/login", async (HttpContext context, AuthStore auth, SessionStore sessionStore) =>
{
    var request = context.Request;
    var body = await ReadJsonBody(request);
    var user = CleanUserName(GetBodyString(body, "user"));
    var password = GetBodyString(body, "password");
    var profile = auth.Get(user);
    if (auth.IsDisabled(user)) return Results.Json(new { ok = false, error = "账号已停用，请联系管理员" }, statusCode: 403);
    if (profile is null)
    {
        if (!user.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            // 不再区分"账号不存在(404)"与"密码不正确(401)"——那是用户名枚举预言机
            return Results.Json(new { ok = false, error = "账号或密码不正确" }, statusCode: 401);
        }
        if (password != paths.DefaultLoginPassword)
        {
            return Results.Json(new { ok = false, error = "账号或密码不正确" }, statusCode: 401);
        }
        var token = sessionStore.SignIn(context, user);
        return Results.Json(new { ok = true, user, mustChangePassword = true, token });
    }
    if (!auth.Verify(password, profile)) return Results.Json(new { ok = false, error = "账号或密码不正确" }, statusCode: 401);
    // 旧档案（单轮 SHA256）登录成功后自动升级为 PBKDF2 并回写
    if (auth.NeedsHashUpgrade(profile)) auth.UpgradeHash(user, password, profile);
    var loginToken = sessionStore.SignIn(context, user);
    return Results.Json(new { ok = true, user, mustChangePassword = auth.MustChangePassword(profile), token = loginToken });
}).RequireRateLimiting("login");

app.MapPost("/api/auth/change-password", async (HttpContext context, AuthStore auth, SessionStore sessionStore) =>
{
    var request = context.Request;
    var body = await ReadJsonBody(request);
    var user = CleanUserName(Convert.ToString(context.Items[SessionStore.UserItemKey], CultureInfo.InvariantCulture) ?? "");
    var oldPassword = GetBodyString(body, "oldPassword");
    var newPassword = GetBodyString(body, "newPassword");
    if (newPassword.Length < 6) return Results.Json(new { ok = false, error = "新密码至少 6 位" }, statusCode: 400);
    var profile = auth.Get(user);
    var oldOk = profile is null ? oldPassword == paths.DefaultLoginPassword : auth.Verify(oldPassword, profile);
    if (!oldOk) return Results.Json(new { ok = false, error = "原密码不正确" }, statusCode: 401);
    var next = auth.CreateProfile(newPassword);
    next["mustChangePassword"] = false;
    /* 换凭据版本：这一步是"吊销的耐久性"来源。就算紧接着 sessions.json 写失败、
       旧会话记录还留在磁盘上，重启后解析时版本对不上 → 旧令牌一样进不来。 */
    AuthStore.RotateCredentialVersion(next);
    auth.Set(user, next);
    auth.Save();
    // 管理员口令已不再是初始口令，初始口令文件（若还在）就是过期信息。
    if (user.Equals("admin", StringComparison.OrdinalIgnoreCase)) paths.DeleteInitialAdminPasswordFile();
    // 改密即止损：先吊销该用户全部既有会话，再为本机重新签发一张
    var revokeOnChange = sessionStore.RevokeUser(user);
    var newToken = sessionStore.SignIn(context, user);
    return Results.Json(new { ok = true, user, token = newToken, sessionRevoke = SessionRevokeReport(revokeOnChange) });
}).RequireRateLimiting("login");

app.MapPost("/api/auth/logout", (HttpContext context, SessionStore sessionStore) =>
{
    sessionStore.SignOut(context);
    return Results.Json(new { ok = true });
});

app.MapGet("/api/user/load", async (HttpContext context, string? user, UserDataStore userData) =>
{
    string clean;
    try { clean = ResolveRequestedUser(context, user); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    try
    {
        var loaded = await userData.LoadAsync(clean);
        return Results.Json(new { ok = true, user = clean, data = loaded.Data, revision = loaded.Revision });
    }
    catch (Exception ex)
    {
        /* ⚠️ 读失败**绝不能**伪装成「这个账号没有数据」。
           旧实现返回 200 + {data:{},revision:""}，前端就把空包当基线：等读恢复正常，
           用户随手做一步操作，下一次整包保存就把真实进度整包覆盖掉
           （实测 supa.json 2651 → 700 字节，四门课的 answers/notes/verified/dailyActivity 全丢）。
           这里返回 5xx，前端据此进只读态、不写回。 */
        SafeErrorText(ex);   // 只借它把完整堆栈记进服务端日志，细节不外泄
        return Results.Json(new { ok = false, error = "读取用户数据失败" }, statusCode: 500);
    }
});

/* 限长读取请求体（UTF-8）：超限返回 null，由调用方回 400。
   为什么要限长：客户端会把保存请求 gzip 压缩后再发（约 1/10 体积），
   请求体经 UseRequestDecompression 还原后才到这里，压缩比可以被构造成任意大。 */
static async Task<string?> ReadBodyWithLimitAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
{
    using var buffer = new MemoryStream();
    var chunk = new byte[64 * 1024];
    while (true)
    {
        var read = await stream.ReadAsync(chunk, cancellationToken);
        if (read <= 0) break;
        if (buffer.Length + read > maxBytes) return null;
        buffer.Write(chunk, 0, read);
    }
    return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
}

app.MapPost("/api/user/save", async (HttpContext context, string? user, UserDataStore userData) =>
{
    var request = context.Request;
    string clean;
    try { clean = ResolveRequestedUser(context, user); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    /* ContentLength 是「线上字节数」：带 Content-Encoding: gzip 时它是压缩后的长度，
       所以真正的 32MB 上限要看解压后的实际字节数 —— 读的时候限长，别等 ReadToEndAsync
       把解压炸弹整个吃进内存再判。 */
    if (request.ContentLength > 32L * 1024 * 1024) return Results.Json(new { ok = false, error = "用户数据超过 32MB，请清理历史记录或导出归档" }, statusCode: 400);
    var body = await ReadBodyWithLimitAsync(request.Body, 32 * 1024 * 1024, context.RequestAborted);
    if (body is null) return Results.Json(new { ok = false, error = "用户数据超过 32MB，请清理历史记录或导出归档" }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(body)) body = "{}";
    // 必须是 JSON 对象（以前接受数组/标量并落盘，导出与备份的消费者都要跟着防）
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Results.Json(new { ok = false, error = "用户数据必须是 JSON 对象" }, statusCode: 400);
        }
    }
    catch (JsonException ex)
    {
        return Results.Json(new { ok = false, error = "用户数据不是有效 JSON：" + ex.Message }, statusCode: 400);
    }
    /* 前置条件必须**显式**，三种写法都收：
         · If-Match: "<revision>"   常规：内容必须与客户端手里的 revision 一致
         · If-None-Match: *         标准 HTTP「只在资源不存在时创建」
         · If-Match: "" / expectEmpty=1 / ?revision=（空值）  「客户端这边没有数据」
       ⚠️ 最后一种以前会被当成「无前置条件」直接写 —— 那正是覆盖用户真实进度的那条路
       （读失败 → revision="" → 下一次保存把 2651 字节整包换成 700 字节）。
       完全不带前置条件（老客户端）行为不变。 */
    var hasIfNoneMatch = request.Headers.TryGetValue("If-None-Match", out var ifNoneMatchValues);
    var hasIfMatch = request.Headers.TryGetValue("If-Match", out var ifMatchValues);
    var ifNoneMatch = ifNoneMatchValues.ToString();
    var ifMatch = ifMatchValues.ToString();
    var expectedRevision = "";
    var expectEmpty = false;
    if (hasIfNoneMatch && ifNoneMatch.Contains('*', StringComparison.Ordinal))
    {
        expectEmpty = true;
    }
    else if (hasIfMatch)
    {
        var tag = ifMatch.Trim();
        if (tag != "*")
        {
            tag = tag.Trim('"').Trim();
            if (tag.Length == 0) expectEmpty = true;      // If-Match: "" = 「我这边是空的」
            else expectedRevision = tag;
        }
    }
    else if (request.Query.ContainsKey("revision"))
    {
        var revisionQuery = (Convert.ToString(request.Query["revision"], CultureInfo.InvariantCulture) ?? "").Trim();
        if (revisionQuery.Length > 0) expectedRevision = revisionQuery;
        else expectEmpty = true;                          // ?revision= 空值 = 「我这边是空的」
    }
    else if (request.Query.ContainsKey("expectEmpty"))
    {
        var flag = (Convert.ToString(request.Query["expectEmpty"], CultureInfo.InvariantCulture) ?? "").Trim();
        expectEmpty = flag is "1" or "true" or "True";
    }
    try
    {
        var revision = await userData.SaveAsync(clean, body, expectedRevision, expectEmpty);
        return Results.Json(new { ok = true, user = clean, revision, savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
    }
    catch (UserDataConflictException)
    {
        return Results.Json(new { ok = false, error = "用户数据已在其他页面更新，请刷新后重试", code = "user_data_conflict" }, statusCode: 409);
    }
});

app.MapPost("/api/user/import", async (HttpContext context, string? user, UserDataStore userData) =>
{
    /* 恢复用户自己导出的备份。
       以前「导出我的数据」导出的 JSON 没有任何回灌路径 —— 全站文件输入只收 .zip（管理员用），
       用户以为存了备份，真出事时恢复不了。这个端点补上另一半。

       与 /api/user/save 的三点不同：
         1. 接受导出包 {exportedAt,user,revision,data}，剥掉外层只落 data（也接受裸 data 对象）；
         2. 默认**不覆盖**已有数据 —— 必须显式带 force=1 才覆盖，否则 409。
            覆盖会丢掉「导出之后新做的题」，这是不可逆的，不能靠一个默认真值决定；
         3. 覆盖时仍然走 If-Match，前端拿的是导入前的 revision，与页面上的编辑并发安全。 */
    var request = context.Request;
    string clean;
    try { clean = ResolveRequestedUser(context, user); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    if (request.ContentLength > 32L * 1024 * 1024)
    {
        return Results.Json(new { ok = false, error = "备份文件超过 32MB" }, statusCode: 400);
    }
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    if (Encoding.UTF8.GetByteCount(body) > 32 * 1024 * 1024)
    {
        return Results.Json(new { ok = false, error = "备份文件超过 32MB" }, statusCode: 400);
    }
    if (string.IsNullOrWhiteSpace(body)) return Results.Json(new { ok = false, error = "备份文件是空的" }, statusCode: 400);

    JsonDocument doc;
    try { doc = JsonDocument.Parse(body); }
    catch (JsonException ex) { return Results.Json(new { ok = false, error = "备份文件不是有效 JSON：" + ex.Message }, statusCode: 400); }
    using (doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Results.Json(new { ok = false, error = "备份文件必须是 JSON 对象" }, statusCode: 400);

        JsonElement data;
        string exportedFrom = "";
        if (root.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
        {
            data = inner;
            if (root.TryGetProperty("user", out var fromUser) && fromUser.ValueKind == JsonValueKind.String)
            {
                exportedFrom = (fromUser.GetString() ?? "").Trim();
            }
        }
        else
        {
            // 裸 data：管理员 zip 里就是这个形状，也允许直接贴。
            data = root;
        }

        /* 拦一次明显贴错的东西：导出包里一定有 courses（哪怕为空对象）。
           没有就说明这是别的 JSON，落盘只会把用户进度换成垃圾。 */
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("courses", out _))
        {
            return Results.Json(new { ok = false, error = "这不是云习题库的备份文件（缺少 courses 字段）" }, statusCode: 400);
        }

        var force = (Convert.ToString(request.Query["force"], CultureInfo.InvariantCulture) ?? "").Trim() is "1" or "true" or "True";
        string expectedRevision = "";
        if (request.Headers.TryGetValue("If-Match", out var ifMatchValues))
        {
            var tag = ifMatchValues.ToString().Trim();
            if (tag != "*") expectedRevision = tag.Trim('"').Trim();
        }

        // 先看现有数据：没有就直接写；有就要求 force。
        /* ⚠️ 「有没有数据」不能用类型判断：首次用户 LoadAsync 回的是空 Dictionary，
           而落盘读回来的可能是 JsonObject / JsonElement / Dictionary，形状不固定。
           统一按「JSON 对象里有没有键」判，另外把 revision 为空也算作没有数据。 */
        var existing = await userData.LoadAsync(clean);
        var hasExisting = existing.Revision.Length > 0 && HasAnyProperty(existing.Data);
        if (hasExisting && !force)
        {
            return Results.Json(new
            {
                ok = false,
                code = "user_data_exists",
                error = exportedFrom.Length > 0 && !exportedFrom.Equals(clean, StringComparison.OrdinalIgnoreCase)
                    ? $"这个备份来自账号「{exportedFrom}」，导入会把「{clean}」现有的进度整体替换掉"
                    : "导入会把这个账号现有的进度整体替换掉",
                revision = existing.Revision,
                exportedFrom,
            }, statusCode: 409);
        }

        try
        {
            var revision = await userData.SaveAsync(clean, data.GetRawText(), expectedRevision, expectEmpty: false);
            return Results.Json(new { ok = true, user = clean, revision, importedFrom = exportedFrom });
        }
        catch (UserDataConflictException)
        {
            return Results.Json(new { ok = false, error = "用户数据已在其他页面更新，请刷新后重试", code = "user_data_conflict" }, statusCode: 409);
        }
    }
});

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

// ── 章节映射（外挂，见 ChapterMapStore 的注释） ─────────────────────────────
// 整卷类卷册（历年真题/模考/预测…）的题在题库里没有章节归属，靠外挂映射补。
// 映射落 data/chapter-map.json，不写进 question-bank.db（整包上传会替换那个文件）。
app.MapGet("/api/chapter-map", (ChapterMapStore chapterMap) =>
{
    var map = chapterMap.Load();
    return Results.Json(new { ok = true, generatedAt = map.GeneratedAt, chapters = map.Chapters, map = map.Map });
});

/* 这里原有 /api/admin/chapter-map/status 与 /rebuild 两个端点，前端从未调用过：
   章节映射的重建由启动自检、换题库、保存题目三处内部触发，管理员没有手动重建的需要。
   已删除（保留 ChapterMapStore 内部实现）。 */

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

/// <summary>
/// 上游课程目录（失败**不抛**）：管理端列表宁可少几门「未拉取」的课，
/// 也不能因为上游抖动 / 账号没配就整个列表打不开。
/// </summary>
static async Task<Puller.CourseCatalogSnapshot?> TryLoadUpstreamCatalogAsync(IConfiguration configuration)
{
    try
    {
        var user = Puller.BankCredentials.ResolveUser(configuration["App:BankServiceUser"]);
        var password = Puller.BankCredentials.ResolvePassword(configuration["App:BankServicePassword"]);
        Puller.BankCredentials.EnsureConfigured(user, password);
        using var soap = new Puller.SoapClient(
            Puller.BankCredentials.ResolveUrl(configuration["App:BankServiceUrl"]), user, password, 15000);
        return await Puller.CourseCatalog.LoadFullAsync(soap);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[catalog] 上游课程目录不可用，管理端仅列本地课程：{SafeErrorText(ex)}");
        return null;
    }
}

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

/* ── 每日推送提醒 ────────────────────────────────────────────────────────
   配置存 userdata/push/<user>.json（**子目录**）。为什么不写进用户数据整包、为什么
   不能放顶层，见文件末尾 PushConfig 的注释。
   四个端点都走 ResolveRequestedUser：非管理员拿到的永远是自己，管理员可用 ?user= 指定。 */
app.MapGet("/api/push/config", (HttpContext context, PushConfigStore store, string? user) =>
{
    string clean;
    try { clean = ResolveRequestedUser(context, user); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    return Results.Json(store.ReadForApi(clean));
});

app.MapPut("/api/push/config", async (HttpContext context, PushConfigStore store, string? user) =>
{
    string clean;
    try { clean = ResolveRequestedUser(context, user); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    var body = await new StreamReader(context.Request.Body, Encoding.UTF8).ReadToEndAsync();
    PushConfig incoming;
    try { incoming = PushConfigStore.Parse(body); }
    catch (JsonException ex) { return Results.Json(new { ok = false, error = "推送配置不是有效 JSON：" + ex.Message }, statusCode: 400); }
    try
    {
        return Results.Json(store.Save(clean, incoming));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
    }
});

/* 「发送测试」：用当前配置立刻发一条，**不写** lastSentDate。
   开关关着也能测 —— 用户改完地址第一件事就是想验证通不通，不该先逼他打开总开关。 */
app.MapPost("/api/push/test", async (HttpContext context, PushConfigStore store, UserDataStore userData, WebPushSender webPush, string? user) =>
{
    string clean;
    try { clean = ResolveRequestedUser(context, user); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    var cfg = store.Read(clean);
    /* webpush 没有「地址」这个概念（凭据是每台设备的订阅），所以这条前置检查要跳过，
       否则浏览器通知永远点不动「发送测试」。 */
    if (cfg.Channel.Type != "webpush" && string.IsNullOrWhiteSpace(cfg.Channel.Url))
    {
        return Results.Json(new { ok = false, error = "还没有填写推送地址" }, statusCode: 400);
    }
    var now = DateTime.Now;
    var digest = await PushDigestReader.ComputeAsync(userData, clean, now) ?? new PushDigest { DaysLeft = -1 };
    /* 今天已经练过 + style=remind 时 Build 会返回 null（「今天不发」），但用户点的是
       「发送测试」—— 必须发出去一条，否则他会以为配置坏了。给一条明确的测试消息。 */
    var message = PushMessageBuilder.Build(cfg, digest, now, test: true)
        ?? new PushMessage("【测试】提醒已配置好",
            digest.DoneToday > 0 ? $"今天已练 {digest.DoneToday} 题，之后不会再打扰你" : "正式提醒会在每天 " + cfg.Time + " 发出",
            PushMessageBuilder.UrlFor(cfg.BaseUrl));
    var (ok, error) = await PushDelivery.SendAsync(webPush, clean, cfg.Channel, message, CancellationToken.None);
    return Results.Json(new
    {
        ok,
        error,
        message = new { title = message.Title, body = message.Body, url = message.Url }
    });
});

/* ── 浏览器通知（Web Push）的绑定 ────────────────────────────────────────
   为什么单独三个端点而不是并进 PUT /api/push/config：订阅是**每台设备一份凭据**，
   走整包保存必然出现「手机保存一次就把电脑那份冲掉」。 */
app.MapGet("/api/push/vapid", (HttpContext context, WebPushSender webPush) =>
{
    /* 公钥本来就是公开的（它要发到每台设备上），但仍然要求已登录：
       没登录的人不需要知道这个站开了推送。ResolveRequestedUser 只用来验会话。 */
    try { ResolveRequestedUser(context, null); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    return Results.Json(new { ok = true, publicKey = webPush.PublicKey });
});

app.MapPost("/api/push/subscribe", async (HttpContext context, PushConfigStore store, string? user) =>
{
    string clean;
    try { clean = ResolveRequestedUser(context, user); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    var body = await new StreamReader(context.Request.Body, Encoding.UTF8).ReadToEndAsync();
    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        /* 前端直接 POST PushSubscription.toJSON()，所以同时接受
           { endpoint, keys:{p256dh,auth} } 和包一层 { subscription: {...} } 两种形状。 */
        var source = root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("subscription", out var nested)
                     && nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;
        var subscription = new PushSubscription
        {
            Endpoint = ReadString(source, "endpoint"),
            UserAgent = (context.Request.Headers.UserAgent.ToString() ?? "").Trim(),
        };
        if (source.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
        {
            subscription.P256dh = ReadString(keys, "p256dh");
            subscription.Auth = ReadString(keys, "auth");
        }
        return Results.Json(store.AddSubscription(clean, subscription));
    }
    catch (JsonException ex)
    {
        return Results.Json(new { ok = false, error = "订阅内容不是有效 JSON：" + ex.Message }, statusCode: 400);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
    }
});

app.MapPost("/api/push/unsubscribe", async (HttpContext context, PushConfigStore store, string? user) =>
{
    string clean;
    try { clean = ResolveRequestedUser(context, user); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    var body = await new StreamReader(context.Request.Body, Encoding.UTF8).ReadToEndAsync();
    string? id = null;
    string? endpoint = null;
    try
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            id = doc.RootElement.TryGetProperty("id", out var idValue) ? idValue.ToString() : null;
            endpoint = doc.RootElement.TryGetProperty("endpoint", out var epValue) ? epValue.ToString() : null;
        }
    }
    catch (JsonException ex)
    {
        return Results.Json(new { ok = false, error = "请求内容不是有效 JSON：" + ex.Message }, statusCode: 400);
    }
    var (config, removed) = store.RemoveSubscription(clean, id, endpoint);
    return Results.Json(new { ok = true, removed, config });
});

/* 「服务端看到什么」：把服务端算出的到期数/已练数/连续天数摊开给前端。
   这个端点是**双份实现的探针** —— REVIEW_INTERVAL_DAYS 在 app.js:180 和
   PushDigestReader 各有一份，一旦漂移，这里和错题本的数字会当场对不上。 */
app.MapGet("/api/push/preview", async (HttpContext context, PushConfigStore store, UserDataStore userData, string? user, string? style, string? baseUrl) =>
{
    string clean;
    try { clean = ResolveRequestedUser(context, user); }
    catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
    var now = DateTime.Now;
    var cfg = store.Read(clean);
    /* style / baseUrl 允许用查询参数覆盖：设置页里用户改了「内容风格」但还没保存时，
       预览要跟着变，否则他得先保存才看得到效果。只影响这一次预览，不落盘。 */
    if (!string.IsNullOrWhiteSpace(style)) cfg.Style = style.Trim().ToLowerInvariant();
    /* 空串 = 用户把跳转地址清空了，那按「用服务端默认地址」预览 —— 与真正发送时一致
       （Save 和 Read 都会把空值补成默认值），否则预览里没链接、实际发出去却有。 */
    if (baseUrl is not null)
    {
        var overridden = baseUrl.Trim().TrimEnd('/');
        cfg.BaseUrl = overridden.Length == 0 ? store.DefaultBaseUrl : overridden;
    }
    var digest = await PushDigestReader.ComputeAsync(userData, clean, now);
    if (digest is null)
    {
        return Results.Json(new { ok = false, error = "读取学习数据失败，暂时无法计算今日摘要" }, statusCode: 500);
    }
    var message = PushMessageBuilder.Build(cfg, digest, now);
    return Results.Json(new
    {
        ok = true,
        digest = new
        {
            due = digest.Due,
            wrongTotal = digest.WrongTotal,
            doneToday = digest.DoneToday,
            correctToday = digest.CorrectToday,
            verifiedToday = digest.VerifiedToday,
            streak = digest.Streak,
            daysLeft = digest.DaysLeft,
            dailyTarget = digest.DailyTarget,
            courseName = digest.CourseName,
            daysSinceLast = digest.DaysSinceLast
        },
        willSend = message is not null,
        message = message is null ? null : new { title = message.Title, body = message.Body, url = message.Url }
    });
});

/* 这里原有 POST /api/push/run（管理员强制跑一轮）。删除理由：/api/push/test 已经能
   验证「能不能发出去」，而「为什么今天没发」看 docker logs 里调度器那几行更直接。
   留着它多一个能群发全站提醒的接口，收益不抵风险。 */

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

internal static class AppHelpers
{
internal static async Task<Dictionary<string, object?>> ReadJsonBody(HttpRequest request)
{
    try
    {
        var data = await JsonSerializer.DeserializeAsync<Dictionary<string, object?>>(request.Body);
        return data ?? new Dictionary<string, object?>();
    }
    catch (JsonException ex)
    {
        // 交给全局异常处理 → 400，而不是裸 500 空响应体
        throw new InvalidOperationException("请求体不是有效 JSON：" + ex.Message);
    }
}

/// <summary>当前会话的用户名（中间件已把会话用户放进 Items；取不到返回空串）。</summary>
internal static string SessionUser(HttpContext context)
    => Convert.ToString(context.Items[SessionStore.UserItemKey], CultureInfo.InvariantCulture) ?? "";

/// <summary>从 JSON 对象里读一个字符串字段；缺失或类型不对都回空串（不抛）。</summary>
internal static string ReadString(JsonElement element, string name)
{
    return element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
        ? (value.GetString() ?? "")
        : "";
}

/// <summary>
/// 反序列化出来的用户数据里有没有任何字段。
/// 形状不固定（首次用户是空 Dictionary，落盘读回来可能是 JsonObject / JsonElement），
/// 所以只能按「各自的对象模型数一下键」来判断，不能靠类型分支。
/// </summary>
internal static bool HasAnyProperty(object? data)
{
    switch (data)
    {
        case null:
            return false;
        case System.Text.Json.Nodes.JsonObject node:
            return node.Count > 0;
        case JsonElement element:
            return element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Any();
        case System.Collections.IDictionary dict:
            return dict.Count > 0;
        case System.Collections.IEnumerable seq:
            return seq.Cast<object?>().Any();
        default:
            return true;
    }
}

internal static string GetBodyString(Dictionary<string, object?> body, string name)
{
    return body.TryGetValue(name, out var value) ? ScalarString(value) : "";
}

internal static string ScalarString(object? value)
{
    if (value is null || value is DBNull) return "";
    if (value is JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }
    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}

internal static bool ScalarBool(object? value)
{
    if (value is null || value is DBNull) return false;
    if (value is JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.True) return true;
        if (element.ValueKind is JsonValueKind.False) return false;
        if (element.ValueKind is JsonValueKind.String) return bool.TryParse(element.GetString(), out var parsed) && parsed;
        if (element.ValueKind is JsonValueKind.Number && element.TryGetInt32(out var number)) return number != 0;
        return false;
    }
    if (value is bool b) return b;
    if (value is int i) return i != 0;
    if (value is long l) return l != 0;
    return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var result) && result;
}

/// <summary>
/// 整类不展示的课程分类（默认「合作专区」——里面是 5210 / 会计365 / 钟辉芳 这类杂项，不是正式题库）。
/// 配置键 <c>App:ExcludedCourseClasses</c>，逗号分隔可覆盖；留空字符串表示不排除任何分类。
/// </summary>
internal static List<string> ExcludedCourseClasses(IConfiguration configuration)
{
    var raw = configuration["App:ExcludedCourseClasses"] ?? DefaultExcludedCourseClasses;
    if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
    return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}

/// <summary>默认排除的分类。</summary>
internal const string DefaultExcludedCourseClasses = "合作专区";

/// <summary>
/// 不能用作账号的保留名：与用户数据目录里的系统文件同名。
/// 例如 sessions → sessions.json 是登录会话存储，当成账号建/删会破坏会话。
/// </summary>
internal static bool IsReservedUserName(string name)
    => name.Equals("sessions", StringComparison.OrdinalIgnoreCase)
    || name.Equals("accounts", StringComparison.OrdinalIgnoreCase);

/// <summary>
/// 打开题库连接，并把「拉取库」（未购课程的题库）ATTACH 进来、建同名 TEMP VIEW 遮蔽主表。
/// 不带库名的读查询会透明地走视图（主库优先，拉取库补主库没有的课程），所以读路径不用改；
/// **写入必须显式带 <c>main.</c> / <c>pulled.</c> 库名**——TEMP VIEW 不可写。
/// </summary>
internal static SqliteConnection OpenBank(string mainDbPath, string? pulledDbPath = null, bool readOnly = true)
{
    var conn = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = mainDbPath,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        DefaultTimeout = 30,
    }.ToString());
    conn.Open();
    AttachPulledBank(conn, pulledDbPath);
    return conn;
}

/// <summary>把拉取库挂到 <c>pulled</c> 并建遮蔽视图；库不存在就什么都不做（行为与拆库前一致）。</summary>
internal static void AttachPulledBank(SqliteConnection conn, string? pulledDbPath)
{
    if (string.IsNullOrWhiteSpace(pulledDbPath) || !File.Exists(pulledDbPath)) return;
    if (string.Equals(Path.GetFullPath(pulledDbPath), Path.GetFullPath(conn.DataSource ?? ""), StringComparison.Ordinal)) return;

    // 连接来自连接池：底层句柄可能已经附加过 pulled，重复 ATTACH 会报 "database pulled is already in use"
    var attached = false;
    using (var probe = conn.CreateCommand())
    {
        probe.CommandText = "select count(*) from pragma_database_list where name = 'pulled'";
        attached = Convert.ToInt32(probe.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }
    if (!attached)
    {
        using var attach = conn.CreateCommand();
        // 拉取库本来就是"拉取写入"的目标，必须是可写目录；这里按普通路径附加即可。
        attach.CommandText = "attach database $p as pulled";
        attach.Parameters.AddWithValue("$p", pulledDbPath);
        attach.ExecuteNonQuery();
    }

    // 课程级优先：主库有正文（coursesubject 有行）的课整门用主库，其余整门用拉取库；
    // 两边都没有正文的课仍从主库取 course 行，否则管理页的课程列表会缺课。
    string[] statements =
    {
        "create temp view if not exists _bank_main_courses as select distinct icourseid from main.coursesubject",
        """
        create temp view if not exists course as
            select * from main.course where icourseid in (select icourseid from _bank_main_courses)
            union all
            select * from pulled.course where icourseid not in (select icourseid from _bank_main_courses)
            union all
            select * from main.course where icourseid not in (select icourseid from _bank_main_courses)
                                        and icourseid not in (select icourseid from pulled.course)
        """,
        """
        create temp view if not exists coursechapter as
            select * from main.coursechapter where icourseid in (select icourseid from _bank_main_courses)
            union all
            select * from pulled.coursechapter where icourseid not in (select icourseid from _bank_main_courses)
        """,
        """
        create temp view if not exists coursesubject as
            select * from main.coursesubject where icourseid in (select icourseid from _bank_main_courses)
            union all
            select * from pulled.coursesubject where icourseid not in (select icourseid from _bank_main_courses)
        """,
        """
        create temp view if not exists courseclass as
            select * from main.courseclass
            union all
            select * from pulled.courseclass where iclassid not in (select iclassid from main.courseclass)
        """,
        """
        create temp view if not exists coursesubclass as
            select * from main.coursesubclass
            union all
            select * from pulled.coursesubclass where isubclassid not in (select isubclassid from main.coursesubclass)
        """,
        """
        create temp view if not exists coursesubjecttype as
            select * from main.coursesubjecttype
            union all
            select * from pulled.coursesubjecttype where isubjecttype not in (select isubjecttype from main.coursesubjecttype)
        """,
    };
    foreach (var sql in statements)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 视图已存在之类：忽略（连接是短生命周期，正常不会走到）。
        }
    }
}

internal static List<int> ParseIdList(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return new List<int>();
    return value.Split(',')
        .Select(part => int.TryParse(part.Trim(), out var id) ? id : 0)
        .Where(id => id > 0)
        .Distinct()
        .Take(30000)
        .ToList();
}

internal static bool IsTruthy(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return false;
    value = value.Trim().ToLowerInvariant();
    return value is "1" or "true" or "yes" or "on";
}

// 空串/null 一律不是 admin。以前 CleanUserName("") 会返回 "admin"，导致 IsAdmin(null)==true。
internal static bool IsAdmin(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return false;
    return CleanUserName(value).Equals("admin", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 用**会话身份**判断是不是管理员。比 <see cref="IsAdmin"/> 可信：
/// `IsAdmin` 只看 URL 上的 `?user=`，那是客户端可控的，任何已登录用户
/// 都能传 `?user=admin` 冒充管理员；会话身份是中间件校验过的。
/// </summary>
internal static bool IsAdminRequest(HttpContext context)
{
    var sessionUser = Convert.ToString(context.Items[SessionStore.UserItemKey], CultureInfo.InvariantCulture) ?? "";
    return sessionUser.Equals("admin", StringComparison.OrdinalIgnoreCase);
}

/// <summary>同上，但直接收 <see cref="HttpRequest"/>——处理函数只拿到 request 时用。</summary>
internal static bool IsAdminRequest(HttpRequest request) => IsAdminRequest(request.HttpContext);

// 只返回错误文案字符串。以前返回的是对象 `{ok, error}`，而调用处写成 `error = SafeError(ex)`，
// 于是响应体变成 `{"error":{"ok":false,"error":"…"}}` 的**嵌套对象**，
// 前端 `new Error(err.error)` 拿到对象 → toast 显示 "[object Object]"，用户完全看不懂。
// 受影响端点：/api/admin/user-courses、/api/admin/course-update-check、/api/admin/update-bank。
internal static string SafeError(Exception ex) => SafeErrorText(ex);

/// <summary>只取错误文案（进度登记等处用，不能直接塞对象）。</summary>
internal static string SafeErrorText(Exception ex)
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {ex}");
    /* 凭据库损坏：**不要**把详细文案（含绝对路径、解析器内部信息）回给调用方。
       这条路径可能被未登录请求触发（/api/auth/session 一进来就解析 AuthStore），
       回显绝对路径等于免费泄露部署目录结构。完整原因已写进上面的日志。 */
    if (ex is AuthStoreUnavailableException) return "账号数据暂时不可用，请联系管理员（服务端日志有详细信息）";
    if (ex is InvalidOperationException || ex is UserDataConflictException) return ex.Message;
    // 本地改动叠层读/写失败：文案本身就是"该怎么办"，透传（这些接口都是管理员接口）
    if (ex is Puller.LocalEditsUnavailableException) return ex.Message;
    // 以前这些都被压成"操作失败，请稍后重试"，用户无法自诊断；这里透传（不含机密）
    if (ex is IOException) return "文件读写失败：" + ex.Message;
    if (ex is UnauthorizedAccessException) return "文件权限不足：" + ex.Message;
    if (ex is InvalidDataException) return "数据格式不正确：" + ex.Message;
    if (ex is SqliteException) return "题库数据库操作失败：" + ex.Message;
    if (ex is JsonException) return "JSON 格式不正确：" + ex.Message;
    return "操作失败，请稍后重试";
}

internal static string ResolveRequestedUser(HttpContext context, string? requested)
{
    var sessionUser = Convert.ToString(context.Items[SessionStore.UserItemKey], CultureInfo.InvariantCulture) ?? "";
    if (sessionUser.Length == 0) throw new UnauthorizedAccessException("登录已失效");
    if (!sessionUser.Equals("admin", StringComparison.OrdinalIgnoreCase)) return CleanUserName(sessionUser);
    var target = string.IsNullOrWhiteSpace(requested) ? "admin" : CleanUserName(requested);
    // 保留名（sessions.json 是会话存储，不是用户）：以前这里没查，导致
    // ?user=sessions 能读/写会话文件本身（伪造持久凭据）。建号/分配题库都查了，这里也必须查。
    if (IsReservedUserName(target))
    {
        throw new InvalidOperationException($"「{target}」是系统保留名称，不能作为账号");
    }
    return target;
}

internal static string NormalizeDataType(string? value)
{
    var type = (value ?? "").Trim().ToLowerInvariant();
    if (type is "bank" or "question-bank" or "questions") return "bank";
    if (type is "userdata" or "user-data" or "users") return "userdata";
    throw new InvalidOperationException("未知的数据类型");
}

internal static string CleanUserName(string? value)
{
    var name = (value ?? "").Trim();
    if (name.Length == 0) name = "admin";
    name = Regex.Replace(name, @"[^\w\-.\u4e00-\u9fff]", "_");
    return name.Length > 40 ? name[..40] : name;
}

internal static string UserDataPath(AppPaths paths, string user) => Path.Combine(paths.UserDataRoot, CleanUserName(user) + ".json");
internal static string NowText() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

/// <summary>
/// 账号文件清单（顶层 <c>*.json</c>，排除会话文件）。**账号数的唯一来源**：
/// 旧实现有三份各算一遍（/api/admin/users 的列表、/api/admin/data/status 的计数、
/// 前端账号页的 rows.length），任何一处改了判据都会让同屏两个数字打架。
/// 调用方要列表就 <c>ToList()</c>，要数字就 <c>Count()</c>。
/// </summary>
internal static IEnumerable<string> EnumerateUserFiles(AppPaths paths)
    => Directory.Exists(paths.UserDataRoot)
        ? Directory.EnumerateFiles(paths.UserDataRoot, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => !SessionStore.IsSessionFile(path))
        : Enumerable.Empty<string>();

internal static int CountUserFiles(AppPaths paths) => EnumerateUserFiles(paths).Count();

/// <summary>
/// 备份名用：秒级时间戳 + 短随机后缀。同秒内两次备份不再撞名
/// （旧实现是纯秒级 <c>Timestamp()</c>，zip 走 FileMode.CreateNew 会直接抛，目录则被静默覆盖 ——
/// 编辑同一道题同一秒连存两次会覆盖同一份快照，所以那个纯秒级的实现已删除）。
/// </summary>
internal static string TimestampUnique() => DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

/// <summary>把会话吊销结果变成接口字段：removed = 吊销了几张会话，persisted = 会话文件写成功没。
/// 写失败时额外带 warning：那些会话在本进程与**重启后**都已失效（账号状态/凭据版本已经落盘），
/// 但 sessions.json 还是旧内容 —— 必须让调用方和运维看到，不能静默吞掉。</summary>
internal static object SessionRevokeReport(SessionStore.SessionRevocation revoke)
    => revoke.Persisted
        ? (object)new { removed = revoke.Removed, persisted = true }
        : new
        {
            removed = revoke.Removed,
            persisted = false,
            warning = "会话已吊销但持久化失败：" + revoke.PersistError + "；请检查磁盘空间/挂载权限。"
                    + "旧令牌重启后同样会被拒绝（凭据版本已落盘），但 userdata/sessions.json 仍是旧内容。"
        };

/// <summary>把凭据/会话/用户数据文件收成 0600（同机其它用户读不到）。非 Linux 或挂载不支持时忽略。</summary>
internal static void RestrictFilePermissions(string path)
{
    if (!OperatingSystem.IsLinux()) return;
    try
    {
        if (File.Exists(path))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
    catch
    {
        // 某些挂载（FUSE/NFS）不支持 chmod：忽略，不影响功能。
    }
}

internal static void TryDeleteFile(string path)
{
    try
    {
        if (File.Exists(path)) File.Delete(path);
    }
    catch
    {
        // Best-effort cleanup for temporary files.
    }
}

/// <summary>
/// 递归删除目录，失败只当没删掉。用于清理导入的撤销目录：即使残留，
/// 里面的文件名都以 `.stage-` / `old-` 开头、且目录名以 `.` 开头，
/// 不会被用户列表（`*.json` 顶层枚举）或备份（排除 _backups 之外的递归枚举，见下）当成用户数据。
/// </summary>
internal static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    catch
    {
        // Best-effort cleanup for temporary directories.
    }
}

internal static string ContentType(string path)
{
    return Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };
}
}

/// <summary>
/// Pulls one course straight from the upstream ExamClientService into the live bank.
/// Cross-platform: no Access/Jet, no client DLLs, no PowerShell — which is what makes
/// 拉取更新 usable on the Docker/NAS deployment, not just on the Windows server build.
/// </summary>
/// <summary>
/// 题库写操作串行闸：拉取 / 整包上传 / 单课导入 / 清广告 同时只允许一个。
/// 这些操作各自"先删后写"，并发交叠会把课程清成 0 题；这里用进程内锁把它们排开。
/// </summary>
internal static class BankWriteGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>正在执行 + 正在排队等闸的写操作数。只用来告诉界面「前面还有几个任务」。</summary>
    private static int _pending;

    /// <summary>
    /// 取闸，拿不到就等（最多 15 分钟）。
    /// <paramref name="onWaiting"/> 在**开始阻塞之前**同步调用一次，参数是排在我前面的任务数
    /// （0 = 闸空着，不用等）。拉取用它把「排队中」登记进进度记录：排队是阻塞的，
    /// 不提前上报的话，前端在整个等待期间一门课的进度都看不到。
    /// </summary>
    public static IDisposable Enter(Action<int>? onWaiting = null)
    {
        var ahead = Interlocked.Increment(ref _pending) - 1;
        onWaiting?.Invoke(ahead);
        if (!Gate.Wait(TimeSpan.FromMinutes(15)))
        {
            Interlocked.Decrement(ref _pending);
            throw new InvalidOperationException("题库正在被另一个操作占用，请等它结束后重试");
        }
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref _pending);
            Gate.Release();
        }
    }
}

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

/// <summary>
/// SQLite **在线备份**：拿一份一致的库快照，替代 <c>File.Copy(库文件)</c>。
///
/// 为什么必须用它：直接复制活动库的字节，(a) 若源库在 WAL 模式下，<c>-wal</c> 里还没
/// checkpoint 的已提交事务会**漏掉**；(b) 复制期间源库还在被写，拷出来的可能是半截页面。
/// BackupDatabase 走 SQLite 自己的读语义，源库可以继续被读写。
///
/// ⚠️ 它**不是**停写/一致性机制：写操作的互斥仍然由 <see cref="BankWriteGate"/> 负责，
/// 备份只是"不把库拷坏"。所以别拿 <c>SqliteConnection.ClearAllPools()</c> 当停写手段
/// （它只是把空闲池连接关掉，正在服务请求的连接不受影响）。
/// </summary>
internal static class SqliteSnapshot
{
    /// <summary>把 <paramref name="sourcePath"/> 的一致性快照写到 <paramref name="destPath"/>（覆盖已有文件）。</summary>
    public static void Copy(string sourcePath, string destPath)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("题库数据库不存在", source);
        var dest = Path.GetFullPath(destPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
        if (File.Exists(dest)) File.Delete(dest);
        using (var from = Open(source, SqliteOpenMode.ReadOnly))
        using (var to = Open(dest, SqliteOpenMode.ReadWriteCreate))
        {
            from.BackupDatabase(to);
        }
    }

    /// <summary>
    /// 备份连接一律不进连接池：池里的空闲连接在 Windows 上仍占着文件句柄，
    /// 会让随后对备份目录/版本目录的 rename、Delete 失败。
    /// </summary>
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            DefaultTimeout = 30,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }
}

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

static class BackupRetention
{
    public static void Prune(string root, int keepLatest = 20, int keepDays = 30)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, keepDays));
            var entries = Directory.EnumerateFileSystemEntries(root)
                .Select(path => new { Path = path, Updated = File.GetLastWriteTimeUtc(path) })
                .OrderByDescending(item => item.Updated)
                .ToList();
            // 先按数量硬截断。旧写法是"排 20 名之外**且**超过 30 天"才删，
            // 结果当天创建的备份永远不删（线上 _backups 已涨到 1.2GB）。
            foreach (var item in entries.Skip(Math.Max(1, keepLatest))) DeleteEntry(item.Path);
            // 再叠加"太旧就删"，即使还在前 N 名里
            foreach (var item in entries.Take(Math.Max(1, keepLatest)).Where(item => item.Updated < cutoff)) DeleteEntry(item.Path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 备份清理失败: {ex.Message}");
        }
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else if (File.Exists(path)) File.Delete(path);
    }
}

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

sealed class UserDataStore
{
    private readonly AppPaths _paths;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public UserDataStore(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// 取某个用户的**串行锁**。普通 LoadAsync/SaveAsync 内部走同一张表，
    /// 所以只要管理员的写操作也调这里，就会和用户的 load/save 排到同一条队上。
    ///
    /// 为什么必须有这个出口：管理员链路（导入 / 清空 / 删号）以前直接 File.*，
    /// 完全不经过这把锁。于是会出现这样的交错 ——
    ///   用户 load 出旧内容(rev A) → 管理员把文件换成新内容(rev B) → 用户 save(基于 rev A)
    /// 用户那次 save 因为 rev 对不上而撞冲突、被拒（UserDataConflictException），
    /// 但它发生在管理员写完之后，语义上"用户读到的东西"和"他覆盖的东西"已经不是同一份了；
    /// 真正危险的是**管理员清空/删号**：它不带 revision 校验，一旦和用户的写交错，
    /// 就会出现"用户刚保存的进度被清空"或"已删除的账号被一次保存复活"。
    /// 把管理员的写也纳进同一把锁，这类交错就变成确定性的前后关系。
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(string user, Func<Task<T>> work)
    {
        var clean = CleanUserName(user);
        var gate = _locks.GetOrAdd(clean, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>同步版（导入/清空/删号都是文件系统操作，用不上 async 的等待点）。</summary>
    public T RunExclusive<T>(string user, Func<T> work)
    {
        var clean = CleanUserName(user);
        var gate = _locks.GetOrAdd(clean, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            return work();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 管理员的"清空某用户数据"：写回空对象。**在用户的锁内**执行，
    /// 避免和该用户的并发 save 交错（旧实现直接 File.WriteAllText，不带锁）。
    /// </summary>
    public string Clear(string user)
    {
        return RunExclusive(user, () =>
        {
            Directory.CreateDirectory(_paths.UserDataRoot);
            var path = UserDataPath(_paths, user);
            var body = "{}";
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, body, new UTF8Encoding(false));
                RestrictFilePermissions(temp);
                File.Move(temp, path, true);
                RestrictFilePermissions(path);
            }
            finally
            {
                TryDeleteFile(temp);
            }
            return Revision(body);
        });
    }

    /// <summary>
    /// 管理员的"删除某用户数据"：**在用户的锁内**执行。
    /// 必须持锁的原因：删号是"读 auth + 删文件"两步，若与用户 save 交错，
    /// 用户那次 save 会在删除之后把文件写回来（"已删除的账号被保存复活"）。
    /// </summary>
    public void Delete(string user)
    {
        RunExclusive(user, () =>
        {
            var path = UserDataPath(_paths, user);
            if (File.Exists(path)) File.Delete(path);
            return true;
        });
    }

    public async Task<(object Data, string Revision)> LoadAsync(string user)
    {
        var clean = CleanUserName(user);
        var gate = _locks.GetOrAdd(clean, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var path = UserDataPath(_paths, clean);
            string body;
            try
            {
                body = await File.ReadAllTextAsync(path, Encoding.UTF8);
            }
            catch (FileNotFoundException)
            {
                // 首次用户：文件还没建立，这是**正常**的空状态
                return (new Dictionary<string, object?>(), "");
            }
            catch (DirectoryNotFoundException)
            {
                return (new Dictionary<string, object?>(), "");
            }
            /* ⚠️ 除「文件不存在」以外的异常（共享冲突 / 权限 / IO）**必须继续上抛**。
               旧实现先 File.Exists 再读，读失败被外层 catch 吞成「和首次用户一样」→
               路由返回 200 + 空包 → 前端把空包当基线 → 下一次保存整包覆盖真实进度。
               抛出去让路由层回 5xx，前端才有机会进只读态。 */
            if (body.Trim().Length == 0) return (new Dictionary<string, object?>(), "");
            var data = JsonSerializer.Deserialize<object>(body) ?? new Dictionary<string, object?>();
            return (data, Revision(body));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> SaveAsync(string user, string body, string expectedRevision, bool expectEmpty = false)
    {
        var clean = CleanUserName(user);
        var gate = _locks.GetOrAdd(clean, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_paths.UserDataRoot);
            var path = UserDataPath(_paths, clean);
            var currentBody = File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8) : "";
            var currentRevision = currentBody.Length == 0 ? "" : Revision(currentBody);
            if (expectEmpty)
            {
                // 客户端说「我这边没有数据」，可服务端已经有内容 → 冲突，绝不覆盖
                if (currentRevision.Length > 0) throw new UserDataConflictException();
            }
            else if (expectedRevision.Length > 0 && !string.Equals(expectedRevision, currentRevision, StringComparison.OrdinalIgnoreCase))
            {
                throw new UserDataConflictException();
            }

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, body, new UTF8Encoding(false));
                File.Move(temp, path, true);
                RestrictFilePermissions(path);
            }
            finally
            {
                TryDeleteFile(temp);
            }
            return Revision(body);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Revision(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}

sealed class UserDataConflictException : Exception
{
}

/// <summary>
/// 账号库（accounts.dat）不可用：文件存在但读不出来 / 解析失败 / 结构不对。
///
/// 独立成一个类型，是为了让它和普通 InvalidOperationException 走**不同的对外文案**：
/// 这类异常的 Message 里含绝对路径和解析器细节，而它的触发点可能是**未登录请求**
/// （/api/auth/session 一进来就要解析 AuthStore），不能原样回显给调用方。
/// 完整文案仍然写进服务端日志（见 SafeErrorText）。
/// </summary>
sealed class AuthStoreUnavailableException : Exception
{
    public AuthStoreUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>会话存储。两个不变式：
/// 1) 令牌只以 SHA256 落盘（sessions.json 被读到也拿不到可直接用的 cookie 值）。
/// 2) 每条会话记录都带**签发时的账号凭据版本**（<see cref="AuthStore.CredentialVersionOf"/>）：
///    改密/重置/停用/重建账号会换一个新版本，解析时会话版本对不上就作废。
///    这样"吊销"的耐久性由 accounts.dat（原子写、失败上抛）保证，而不是只靠 sessions.json ——
///    后者写失败时旧令牌即使留在磁盘上、进程重启后也不会复活。</summary>
sealed class SessionStore
{
    public const string UserItemKey = "yunxi.session.user";

    /// <summary>会话存储文件名。放在用户数据目录里，但它**不是用户**——
    /// 用户列表/计数/增删用户都要把它排除，否则会冒出一个叫 sessions 的假账号，
    /// 删掉它还会把所有人的登录状态清空。</summary>
    public const string FileName = "sessions.json";

    public static bool IsSessionFile(string pathOrName)
        => string.Equals(Path.GetFileName(pathOrName), FileName, StringComparison.OrdinalIgnoreCase);

    private const string CookieName = "yunxi_session";
    /// <summary>会话令牌的备用传输通道（见 <see cref="SignIn"/>）：cookie 被浏览器吞掉时前端改用它。</summary>
    public const string TokenHeaderName = "X-Session-Token";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);
    private readonly AppPaths _paths;
    private readonly AuthStore _auth;
    private readonly object _fileLock = new();
    private bool _loaded;

    public SessionStore(AppPaths paths, AuthStore auth)
    {
        _paths = paths;
        _auth = auth;
        EnsureLoaded();
    }

    /// <summary>
    /// 签发会话，并把令牌**同时**放进 Set-Cookie 与返回给调用方的字符串里。
    /// 只靠 cookie 不够稳：手机浏览器/内置 WebView 的"云加速"代理会吞掉 Set-Cookie，
    /// 表现为"登录成功但紧接着 401、一直提示登录已失效"。登录响应里带回令牌后，
    /// 前端可用 <see cref="TokenHeaderName"/> 头继续用，不再依赖浏览器是否存了 cookie。
    /// </summary>
    public string SignIn(HttpContext context, string user)
    {
        EnsureLoaded();
        Remove(context.Request);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var clean = CleanUserName(user);
        _sessions[HashToken(token)] = new SessionRecord(clean, DateTimeOffset.UtcNow.Add(Lifetime), AuthStore.CredentialVersionOf(_auth.Get(clean)));
        Persist();
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            /* Lax 而不是 Strict：Strict 会让「从微信/书签/外部链接点进来」的顶级导航
               也不带 cookie，表现为「登录成功但紧接着 401、一直提示登录已失效」。
               Lax 会为顶级导航带上 cookie；跨站 POST 仍不带，且后端还有
               Sec-Fetch-Site 检查兜底，CSRF 防护不受影响。 */
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            MaxAge = Lifetime,
            Path = "/",
            IsEssential = true
        });
        return token;
    }

    /// <summary>优先 cookie；浏览器没存住 cookie 时回退到 <see cref="TokenHeaderName"/> 请求头。</summary>
    public string Resolve(HttpRequest request)
    {
        EnsureLoaded();
        var cookieUser = ResolveToken(ReadCookieToken(request));
        if (cookieUser.Length > 0) return cookieUser;
        return ResolveToken(ReadHeaderToken(request));
    }

    private string ResolveToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var key = HashToken(token);
        if (!_sessions.TryGetValue(key, out var record)) return "";
        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(key, out _);
            Persist();
            return "";
        }
        /* 凭据版本对不上 = 这张会话是改密/重置/停用/重建账号之前签发的。
           正常情况下它早被 RevokeUser 从内存里删掉了；能走到这里说明**磁盘上的旧记录又回来了**
           （进程重启 + sessions.json 持久化失败），所以必须在这里再拦一次。 */
        if (!string.Equals(record.CredentialVersion, AuthStore.CredentialVersionOf(_auth.Get(record.User)), StringComparison.Ordinal))
        {
            _sessions.TryRemove(key, out _);
            Persist();
            return "";
        }
        return record.User;
    }

    /// <summary>请求里到底带了什么凭据（只用于 401 时打诊断日志，不记令牌本身）。</summary>
    public (bool Cookie, bool Header) CredentialPresence(HttpRequest request)
        => (!string.IsNullOrWhiteSpace(ReadCookieToken(request)), !string.IsNullOrWhiteSpace(ReadHeaderToken(request)));

    private static string? ReadCookieToken(HttpRequest request)
        => request.Cookies.TryGetValue(CookieName, out var token) ? token : null;

    private static string? ReadHeaderToken(HttpRequest request)
        => request.Headers.TryGetValue(TokenHeaderName, out var token) ? token.ToString() : null;

    public void Remove(HttpRequest request)
    {
        EnsureLoaded();
        var removed = false;
        foreach (var token in new[] { ReadCookieToken(request), ReadHeaderToken(request) })
        {
            if (!string.IsNullOrWhiteSpace(token) && _sessions.TryRemove(HashToken(token), out _)) removed = true;
        }
        if (removed) Persist();
    }

    /// <summary>吊销某个用户的全部会话（改密/重置/停用/删除时调用）。
    /// 返回值同时报告"腰斩了几张"和"落盘成不成功"——调用方必须把失败透出去，不能吞。</summary>
    public SessionRevocation RevokeUser(string user)
    {
        EnsureLoaded();
        var clean = CleanUserName(user);
        var keys = _sessions
            .Where(kv => kv.Value.User.Equals(clean, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        var removed = 0;
        foreach (var key in keys)
        {
            if (_sessions.TryRemove(key, out _)) removed++;
        }
        var persistError = removed > 0 ? Persist() : null;
        return new SessionRevocation(removed, persistError);
    }

    /// <summary>吊销结果。<see cref="PersistError"/> 非空表示内存里已吊销、sessions.json 没写成：
    /// 旧令牌在本进程**和**重启后都会被账号状态/凭据版本拒掉（fail closed），
    /// 但会话文件仍是旧内容，必须让接口与运维看到（磁盘满/只读）。</summary>
    public readonly record struct SessionRevocation(int Removed, string? PersistError)
    {
        public bool RemovedAny => Removed > 0;
        public bool Persisted => PersistError is null;
    }

    /// <summary>会话令牌只以 SHA256 落盘：sessions.json 被读到也拿不到可直接用的 cookie 值。</summary>
    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public void SignOut(HttpContext context)
    {
        Remove(context.Request);
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_fileLock)
        {
            if (_loaded) return;
            try
            {
                var path = SessionFilePath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var rows = JsonSerializer.Deserialize<List<SessionFileRow>>(json) ?? [];
                    var now = DateTimeOffset.UtcNow;
                    foreach (var row in rows)
                    {
                        if (string.IsNullOrWhiteSpace(row.Token) || string.IsNullOrWhiteSpace(row.User)) continue;
                        if (!DateTimeOffset.TryParse(row.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires)) continue;
                        if (expires <= now) continue;
                        _sessions[row.Token] = new SessionRecord(CleanUserName(row.User), expires, row.CredentialVersion ?? "");
                    }
                }
            }
            catch
            {
                // Ignore corrupt session file and start fresh in-memory.
            }
            _loaded = true;
        }
    }

    /// <summary>把会话表落盘。返回 null = 成功；否则返回失败原因（已同时写日志）。
    /// 旧实现把所有异常静默吞掉：改密/重置/停用后在内存里吊销了、磁盘没写成功，
    /// 容器一重启旧会话就从 sessions.json 里"复活"。持久化失败必须留痕且能回传给接口。</summary>
    private string? Persist()
    {
        lock (_fileLock)
        {
            try
            {
                Directory.CreateDirectory(_paths.UserDataRoot);
                var now = DateTimeOffset.UtcNow;
                var rows = _sessions
                    .Where(kv => kv.Value.ExpiresAt > now)
                    .Select(kv => new SessionFileRow
                    {
                        Token = kv.Key,
                        User = kv.Value.User,
                        ExpiresAt = kv.Value.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                        CredentialVersion = kv.Value.CredentialVersion
                    })
                    .ToList();
                // prune expired from memory too
                foreach (var expired in _sessions.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
                {
                    _sessions.TryRemove(expired, out _);
                }
                var temp = SessionFilePath() + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var json = JsonSerializer.Serialize(rows);
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                File.Move(temp, SessionFilePath(), true);
                RestrictFilePermissions(SessionFilePath());
                return null;
            }
            catch (Exception ex)
            {
                /* 明确记日志：[session-persist-failed] 是给运维和验证装置搜的稳定标记。
                   走 Console.WriteLine（与其它日志同一个流）：Windows 下 stderr 被重定向到文件时
                   中文会退化成 "?"，stdout 不会；docker logs 也只有一个流。 */
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [session-persist-failed] 会话持久化失败（{SessionFilePath()}）：{ex.Message}");
                return ex.Message;
            }
        }
    }

    private string SessionFilePath() => Path.Combine(_paths.UserDataRoot, FileName);

    private sealed record SessionRecord(string User, DateTimeOffset ExpiresAt, string CredentialVersion);

    private sealed class SessionFileRow
    {
        public string Token { get; set; } = "";
        public string User { get; set; } = "";
        public string ExpiresAt { get; set; } = "";
        /// <summary>签发这张会话时的账号凭据版本。老文件里没有这个字段 → 反序列化为 ""，
        /// 与"档案没有版本"的旧账号相等，所以升级不会把所有人踢下线。</summary>
        public string? CredentialVersion { get; set; } = "";
    }
}


sealed class AppPaths
{
    public AppPaths(IConfiguration configuration, IWebHostEnvironment env)
    {
        BaseRoot = env.ContentRootPath;
        PublicRoot = ResolvePublicRoot(configuration["App:PublicRoot"]);
        SqlitePath = Resolve(configuration["App:SqlitePath"] ?? "../data/question-bank.db");
        DataAssetsRoot = Resolve(configuration["App:DataAssetsRoot"] ?? "../data/assets");
        UserDataRoot = Resolve(configuration["App:UserDataRoot"] ?? "../userdata");
        var configured = configuration["App:DefaultLoginPassword"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            DefaultLoginPassword = configured;
        }
        else
        {
            DefaultLoginPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            // 本次生成的口令只有在「管理员账号还不存在」时才是有效的首次登录口令。
            // 管理员已存在时再写这个文件会误导——文件里是刚生成的随机串，而管理员的口令是
            // 早先那次生成（或用户改过的）那个，照文件输是登不进去的。
            var adminProfile = ReadAdminProfile(UserDataRoot);
            if (adminProfile is null)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INIT] 未配置 App:DefaultLoginPassword，本次启动生成的默认登录口令（管理员首次登录用）: {DefaultLoginPassword}");
                TryWriteInitialAdminPassword(UserDataRoot, DefaultLoginPassword);
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INIT] 未配置 App:DefaultLoginPassword；管理员账号已存在，跳过初始口令文件。");
                // 管理员已改过密码 → 残留的初始口令文件一定是过期信息，顺手清掉。
                if (!MustChangePassword(adminProfile)) TryDeleteInitialAdminPassword(UserDataRoot);
            }
        }
    }

    private static void TryWriteInitialAdminPassword(string userDataRoot, string password)
    {
        try
        {
            Directory.CreateDirectory(userDataRoot);
            var path = Path.Combine(userDataRoot, "admin-init-password.txt");
            File.WriteAllText(path, $"管理员首次登录口令：{password}\n（请在首次登录并修改密码后删除本文件）\n", new UTF8Encoding(false));
        }
        catch
        {
            // Best-effort: 文件写入失败不影响服务启动。
        }
    }

    /// <summary>
    /// 读 accounts.dat 里的管理员档案，只用来判断「该不该写初始口令文件」，不做鉴权。
    /// 返回 null 表示还没有管理员账号（首次部署）；解析失败按「已存在」处理，宁可不写也不误删。
    /// </summary>
    private static Dictionary<string, object?>? ReadAdminProfile(string userDataRoot)
    {
        try
        {
            var path = Path.Combine(userDataRoot, "accounts.dat");
            if (!File.Exists(path)) return null;
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object?>>>(File.ReadAllText(path, Encoding.UTF8));
            if (raw is null) return null;
            foreach (var pair in raw)
            {
                if (pair.Key.Equals("admin", StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }
            return null;
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static bool MustChangePassword(Dictionary<string, object?> profile)
        => profile.TryGetValue("mustChangePassword", out var value) && ScalarBool(value);

    /// <summary>管理员改过密码后调用：初始口令文件已过期，清掉避免误导。</summary>
    public void DeleteInitialAdminPasswordFile() => TryDeleteInitialAdminPassword(UserDataRoot);

    private static void TryDeleteInitialAdminPassword(string userDataRoot)
    {
        try
        {
            var path = Path.Combine(userDataRoot, "admin-init-password.txt");
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort: 删不掉不影响服务启动。
        }
    }

    public string BaseRoot { get; }
    public string PublicRoot { get; }
    public string SqlitePath { get; }
    public string DataAssetsRoot { get; }
    public string UserDataRoot { get; }
    public string AuthDataPath => Path.Combine(UserDataRoot, "accounts.dat");
    public string DefaultLoginPassword { get; }

    /// <summary>
    /// 拉取库：与主库同目录、文件名加 <c>-pulled</c> 后缀的独立 SQLite。
    /// 主库是导出产物（整包上传会替换它），拉取来的课程一律写这里，两边互不影响。
    /// </summary>
    public string PulledSqlitePath
    {
        get
        {
            var full = Path.GetFullPath(SqlitePath);
            var dir = Path.GetDirectoryName(full) ?? BaseRoot;
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(full) + "-pulled.db");
        }
    }

    /// <summary>拉取来的题图目录（主图片目录加 <c>-pulled</c> 后缀），避免被整包上传替换掉。</summary>
    public string PulledAssetsRoot =>
        DataAssetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-pulled";

    public string FindAssetSample()
    {
        if (!Directory.Exists(DataAssetsRoot)) return "";
        var file = Directory.EnumerateFiles(DataAssetsRoot, "*", SearchOption.AllDirectories)
            .FirstOrDefault(path => ContentType(path).StartsWith("image/", StringComparison.OrdinalIgnoreCase));
        return file is null ? "" : Path.GetRelativePath(DataAssetsRoot, file).Replace(Path.DirectorySeparatorChar, '/');
    }

    private string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(BaseRoot, path));

    private string ResolvePublicRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Resolve(configured);
        return Resolve("public");
    }
}

sealed class AuthStore
{
    private readonly AppPaths _paths;
    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, object?>> _auth;

    public AuthStore(AppPaths paths)
    {
        _paths = paths;
        _auth = Load();
    }

    public Dictionary<string, object?>? Get(string user)
    {
        lock (_lock) return _auth.TryGetValue(CleanUserName(user), out var profile) ? new Dictionary<string, object?>(profile, StringComparer.OrdinalIgnoreCase) : null;
    }

    public void Set(string user, Dictionary<string, object?> profile)
    {
        lock (_lock) _auth[CleanUserName(user)] = profile;
    }

    public void Remove(string user)
    {
        lock (_lock) _auth.Remove(CleanUserName(user));
    }

    public bool IsDisabled(string user) => IsDisabled(Get(user));

    public bool IsDisabled(Dictionary<string, object?>? profile)
    {
        if (profile is null || !profile.TryGetValue("disabled", out var value) || value is null) return false;
        return ScalarBool(value);
    }

    public bool MustChangePassword(Dictionary<string, object?> profile)
    {
        if (!profile.TryGetValue("mustChangePassword", out var value) || value is null) return false;
        return ScalarBool(value);
    }

    public Dictionary<string, object?> CreateProfile(string password)
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["salt"] = salt,
            ["hash"] = Pbkdf2Hash(password, salt),
            ["hashAlgo"] = "pbkdf2",
            ["changedAt"] = NowText()
        };
    }

    public bool Verify(string password, Dictionary<string, object?> profile)
    {
        var salt = GetProfileString(profile, "salt");
        var hash = GetProfileString(profile, "hash");
        if (salt.Length == 0 || hash.Length == 0) return false;
        var algo = GetProfileString(profile, "hashAlgo");
        var computed = algo == "pbkdf2" ? Pbkdf2Hash(password, salt) : PasswordHash(password, salt);
        return FixedTimeEqualsHex(computed, hash);
    }

    /// <summary>常量时间比较（避免时序侧信道）。</summary>
    private static bool FixedTimeEqualsHex(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(b.ToLowerInvariant()));
    }

    /// <summary>旧档案（无 hashAlgo 或非 pbkdf2）需要在成功登录后升级哈希。</summary>
    public bool NeedsHashUpgrade(Dictionary<string, object?> profile)
        => !GetProfileString(profile, "hashAlgo").Equals("pbkdf2", StringComparison.OrdinalIgnoreCase);

    /// <summary>把档案哈希升级为 PBKDF2（保留 disabled/courses/createdAt 等其它字段）。</summary>
    public void UpgradeHash(string user, string password, Dictionary<string, object?> profile)
    {
        foreach (var pair in CreateProfile(password)) profile[pair.Key] = pair.Value;
        Set(user, profile);
        Save();
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_paths.UserDataRoot);
            var body = JsonSerializer.Serialize(_auth);
            /* 原子写：先写同目录临时文件，再 File.Move(overwrite) 替换。
               旧实现直接 File.WriteAllText 覆盖 accounts.dat —— 写到一半断电/被 kill
               会留下**截断的半个文件**，下次启动 Load() 解析失败 → 全员账号消失。
               与 UserDataStore.SaveAsync 的写法保持一致（同一目录、同卷 rename 才是原子的）。
               ⚠️ 临时文件放 UserDataRoot 下、以 `.` 开头：不会被用户列表（顶层 `*.json`）
               和备份（按 *.json / accounts.dat 收集）当成正式数据。 */
            var temp = _paths.AuthDataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, body, new UTF8Encoding(false));
                RestrictFilePermissions(temp);
                File.Move(temp, _paths.AuthDataPath, true);
                RestrictFilePermissions(_paths.AuthDataPath);
            }
            finally
            {
                TryDeleteFile(temp);
            }
        }
    }

    public void Reload()
    {
        lock (_lock)
        {
            _auth = Load();
        }
    }

    /// <summary>
    /// 读取 accounts.dat。
    ///
    /// ⚠️ 读失败**绝不允许**退化成空账号库。旧实现 catch { return 空字典; }：
    /// 文件损坏/不可读 → 内存里变成"一个账号都没有"，服务照常起来，所有登录 401；
    /// 更糟的是**下一次凭据写入会把空库固化到磁盘**（实测：先是全员登录失败，
    /// 然后一次改密就把 accounts.dat 覆写成只剩 admin 一个账号，u1 永久消失）。
    ///
    /// 现在的口径：
    ///   · 文件不存在            → 空库（首次部署的正常状态，admin 用初始口令走无档案路径）
    ///   · 文件存在但解析/读取失败 → **上抛**，让启动/调用方决定（启动时直接失败比带病运行好）
    /// </summary>
    private Dictionary<string, Dictionary<string, object?>> Load()
    {
        Directory.CreateDirectory(_paths.UserDataRoot);
        if (!File.Exists(_paths.AuthDataPath)) return new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        string text;
        try
        {
            text = File.ReadAllText(_paths.AuthDataPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new AuthStoreUnavailableException($"账号文件 {_paths.AuthDataPath} 无法读取：{ex.Message}；请从备份恢复，不要以空账号库启动", ex);
        }
        // 空文件同样是"不可用"，不能当作"没有账号"——那样会静默清空全员。
        if (text.Trim().Length == 0)
        {
            throw new AuthStoreUnavailableException($"账号文件 {_paths.AuthDataPath} 是空的，拒绝以空账号库启动；请从备份恢复");
        }
        Dictionary<string, Dictionary<string, object?>>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object?>>>(text);
        }
        catch (JsonException ex)
        {
            throw new AuthStoreUnavailableException($"账号文件 {_paths.AuthDataPath} 不是有效 JSON：{ex.Message}；请从备份恢复，不要以空账号库启动", ex);
        }
        // 合法 JSON 但结构不对（[]、{"admin":"oops"}、null）——同样拒绝，别退化成空库
        if (raw is null)
        {
            throw new AuthStoreUnavailableException($"账号文件 {_paths.AuthDataPath} 结构不正确（应为「账号名 → 档案对象」的对象）；请从备份恢复");
        }
        return new Dictionary<string, Dictionary<string, object?>>(raw, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetProfileString(Dictionary<string, object?> profile, string key)
    {
        return profile.TryGetValue(key, out var value) ? ScalarString(value) : "";
    }

    /* ---- 凭据版本（会话耐久性的关键，见 SessionStore） ------------------------------------
       每次「凭据/账号状态发生必须让旧会话失效的变化」都换一个新随机值：
       CreateProfile 之后（新建/改密/重置）、disable。会话记录里存的是**签发时**的这个值，
       解析时会话版本 ≠ 账号当前版本 → 这张会话作废。
       为什么用随机串而不是自增号：账号被删除后重建时，自增号可能撞上旧会话记录里的号
       （旧记录只存在于"吊销时 sessions.json 没写成功"的磁盘上），随机串不会。
       旧档案没有这个字段 → 返回 ""，与会话记录里缺字段的 "" 相等：升级不会踢掉在线用户。 */
    internal const string CredentialVersionKey = "credentialVersion";

    internal static string CredentialVersionOf(Dictionary<string, object?>? profile)
        => profile is null ? "" : GetProfileString(profile, CredentialVersionKey);

    internal static void RotateCredentialVersion(Dictionary<string, object?> profile)
        => profile[CredentialVersionKey] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string PasswordHash(string password, string salt)
    {
        var bytes = Encoding.UTF8.GetBytes(salt + "\n" + password);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Pbkdf2Hash(string password, string salt)
    {
        var saltBytes = Convert.FromBase64String(salt);
        using var derive = new Rfc2898DeriveBytes(password, saltBytes, 100_000, HashAlgorithmName.SHA256);
        var key = derive.GetBytes(32);
        return Convert.ToHexString(key).ToLowerInvariant();
    }
}

/// <summary>
/// 一次更新检查的结论。**三态**，别把 Unknown 混进 Unchanged：
/// 只有 <see cref="Unchanged"/> 才允许跳过拉取，Unknown 一律照拉。
/// </summary>
enum BankUpdateState
{
    /// <summary>确定上游有更新（章节或题目任一有增量）。</summary>
    Updated,

    /// <summary>确定没有更新（两个接口都通、都解析成 0 行）。</summary>
    Unchanged,

    /// <summary>拿不到结论（网络超时 / SOAP 错误 / 返回格式变了）。</summary>
    Unknown,
}

/// <summary>单门课的检查结果。</summary>
readonly record struct BankUpdateCheck(BankUpdateState State, int Chapters, int Subjects, string? Error)
{
    /// <summary>守卫用：**只在确定无更新时**才跳过拉取。</summary>
    public bool IsCertainlyUnchanged => State == BankUpdateState.Unchanged;

    public static BankUpdateCheck Updated(int chapters, int subjects) => new(BankUpdateState.Updated, chapters, subjects, null);
    public static BankUpdateCheck Unchanged() => new(BankUpdateState.Unchanged, 0, 0, null);
    public static BankUpdateCheck Unknown(string error) => new(BankUpdateState.Unknown, 0, 0, error);
}

/// <summary>
/// 检查课程在上游有没有更新：用"增量水位"接口各探一次章节与题目（1~2 次 SOAP/课）。
/// 已知盲区：上游章节改名不更新 <c>dchangedate</c>，这类改动探不出来（客户端同样有这个问题）。
///
/// ⚠️ 两个接口的**载荷格式不一样**，各用各的解析器（这与 PullEngine 的读路径保持一致，
/// 也是这个类原来最大的 bug）：
///   · LoadNewCourseChapter_HadDistrice        → .NET DataTable XML（<see cref="Puller.JkdParsers.ParseDataTable"/>）
///   · LoadNewCourseSubjectGroup_HadDistrict   → pipe 文本（<see cref="Puller.JkdParsers.ParsePipeGroups"/>）
/// 旧实现对题目那份用了 ParseDataTable，解析必然抛 → 被 CountRows 的 catch 吞成 0 行 →
/// "只有题目更新、章节没变"时报"已是最新"，实际拉取被跳过。
/// </summary>
static class CourseUpdateChecker
{
    public static async Task<List<BankUpdateCheck>> CheckAsync(QuestionBank bank, List<int> courseIds, IConfiguration configuration)
    {
        var results = new List<BankUpdateCheck>();
        if (courseIds.Count == 0) return results;
        foreach (var courseId in courseIds)
        {
            results.Add(await CheckOneAsync(bank, courseId, configuration));
        }
        return results;
    }

    /// <summary>
    /// 单门课的检查。**任何异常都转成 <see cref="BankUpdateState.Unknown"/>**，
    /// 绝不转成"没有更新"（旧实现把检查失败封装成 hasUpdate=false，网络抖一下就被判"已是最新"、
    /// 更新按钮点了等于没点）。
    /// </summary>
    public static async Task<BankUpdateCheck> CheckOneAsync(QuestionBank bank, int courseId, IConfiguration configuration)
    {
        try
        {
            // 账号从配置/环境变量来（不再内置在代码里）
            var user = Puller.BankCredentials.ResolveUser(configuration["App:BankServiceUser"]);
            var password = Puller.BankCredentials.ResolvePassword(configuration["App:BankServicePassword"]);
            Puller.BankCredentials.EnsureConfigured(user, password);
            using var soap = new Puller.SoapClient(
                Puller.BankCredentials.ResolveUrl(configuration["App:BankServiceUrl"]), user, password, 30000);

            var marks = bank.CourseWatermarks(courseId);
            var chapterSince = string.IsNullOrWhiteSpace(marks.Chapter) ? "1900-01-01 00:00:00.999" : marks.Chapter!;
            var subjectSince = string.IsNullOrWhiteSpace(marks.Subject) ? "1900-01-01 00:00:00.999" : marks.Subject!;
            var chapterResp = await soap.InvokeAsync("LoadNewCourseChapter_HadDistrice",
                $"<courseId>{courseId}</courseId><courseType>11</courseType><district /><lastUpdateDate>{chapterSince}</lastUpdateDate>");
            var chapterRows = CountChapterRows(chapterResp.Primary);
            var subjectResp = await soap.InvokeAsync("LoadNewCourseSubjectGroup_HadDistrict",
                $"<courseId>{courseId}</courseId><courseType>1</courseType><district /><lastUpdateDate>{subjectSince}</lastUpdateDate>");
            var subjectRows = CountSubjectRows(subjectResp.Primary);
            return chapterRows > 0 || subjectRows > 0
                ? BankUpdateCheck.Updated(chapterRows, subjectRows)
                : BankUpdateCheck.Unchanged();
        }
        catch (Exception ex)
        {
            return BankUpdateCheck.Unknown(SafeErrorText(ex));
        }
    }

    /// <summary>
    /// 单个题库「有没有更新」。给 update-bank 做拉取前守卫用。
    /// **只在确定无更新时返回 false**；拿不到结论（Unknown）返回 true —— 宁可多拉一次，
    /// 也不能因为检查环节自身出错就永远拉不了（这也是这个方法注释里原本的意图）。
    /// </summary>
    public static async Task<bool> HasUpdateAsync(QuestionBank bank, int courseId, IConfiguration configuration)
    {
        var check = await CheckOneAsync(bank, courseId, configuration);
        if (check.State == BankUpdateState.Unknown)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [update-bank] 课程 {courseId} 的更新检查拿不到结论，按「可能有更新」处理，照拉：" +
                              (check.Error ?? ""));
        }
        return !check.IsCertainlyUnchanged;
    }

    /// <summary>
    /// 给前端的形状：**保留 hasUpdate**（老前端/老调用方据此决定"要不要拉"，未知时给 true =
    /// 保守照拉），同时加一个 <c>state</c> 让调用方能区分「确定没更新」与「没结论」。
    /// </summary>
    internal static object ToJson(int courseId, BankUpdateCheck check) => check.State switch
    {
        BankUpdateState.Updated => (object)new
        {
            courseId,
            state = "updated",
            hasUpdate = true,
            chapters = check.Chapters,
            subjects = check.Subjects,
        },
        BankUpdateState.Unchanged => new
        {
            courseId,
            state = "unchanged",
            hasUpdate = false,
            chapters = 0,
            subjects = 0,
        },
        _ => new
        {
            courseId,
            state = "unknown",
            hasUpdate = true,      // 未知 ≠ 无更新
            unknown = true,
            chapters = 0,
            subjects = 0,
            error = check.Error ?? "",
        },
    };

    /// <summary>按请求顺序把检查结果转成响应对象（CheckAsync 的结果与 courseIds 一一对应）。</summary>
    internal static List<object> Describe(List<int> courseIds, List<BankUpdateCheck> checks)
    {
        var results = new List<object>();
        for (var i = 0; i < courseIds.Count && i < checks.Count; i++)
        {
            results.Add(ToJson(courseIds[i], checks[i]));
        }
        return results;
    }

    /// <summary>
    /// 章节增量：.NET DataTable XML。"没有更新"时上游不返回 DataTable（载荷为空）→ 0 行。
    /// 载荷非空却解析不了 → **抛**（格式变了，不能当 0 行 = 已是最新）。
    /// </summary>
    internal static int CountChapterRows(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return 0;
        return Puller.JkdParsers.ParseDataTable(payload).Count;
    }

    /// <summary>
    /// 题目增量：pipe 文本（"start,end,chapterid,type,iindex,score,anscount|..."）。
    /// 空载荷 / "[]" / "0" / "null" 才是真的 0 行；非空但一组都解析不出来 = 格式变了 → 抛。
    /// </summary>
    internal static int CountSubjectRows(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return 0;
        var trimmed = payload.Trim();
        if (trimmed is "[]" or "0" or "null") return 0;
        var groups = Puller.JkdParsers.ParsePipeGroups(trimmed);
        if (groups.Count == 0)
        {
            throw new InvalidOperationException(
                "LoadNewCourseSubjectGroup_HadDistrict 的题目增量不是已知的 pipe 格式（一组都解析不出来），无法判断有没有更新");
        }
        return groups.Sum(group => group.Count);
    }
}

/// <summary>
/// 按用户分配题库：账号档案里的 <c>courses</c> 字段。
/// 缺省（键不存在）= 该用户可见**全部有内容的课程**（兼容旧数据）；数组 = 只可见列出的课程；
/// admin 永远不受限。放 accounts.dat 而不是用户数据文件里——后者用户自己能上传下载，权限放那儿会被改掉。
/// </summary>
static class UserCourses
{
    public const string Field = "courses";

    /// <summary>该用户被允许的课程 id；null = 不限制。</summary>
    public static HashSet<int>? AllowedIds(AuthStore auth, string? user)
    {
        var name = CleanUserName(user);
        if (name.Length == 0 || name.Equals("admin", StringComparison.OrdinalIgnoreCase)) return null;
        var profile = auth.Get(name);
        if (profile is null || !profile.TryGetValue(Field, out var value) || value is null) return null;
        var ids = new HashSet<int>();
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n)) ids.Add(n);
                else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var s)) ids.Add(s);
            }
        }
        else if (value is System.Collections.IEnumerable sequence && value is not string)
        {
            // 注意：List<int> 不实现 IEnumerable<object?>（值类型无协变），必须用非泛型 IEnumerable
            foreach (var item in sequence)
            {
                if (item is null or DBNull) continue;
                if (int.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out var n) && n > 0) ids.Add(n);
            }
        }
        return ids;
    }

    /// <summary>该用户是否能看到这门课（admin / 未分配 → 全部可见）。</summary>
    public static bool IsAllowed(AuthStore auth, string? user, int courseId)
    {
        var allowed = AllowedIds(auth, user);
        return allowed is null || allowed.Contains(courseId);
    }

    /// <summary>保存分配；<paramref name="ids"/> 为 null 表示恢复"全部可见"。</summary>
    public static void Save(AuthStore auth, string user, List<int>? ids)
    {
        var name = CleanUserName(user);
        if (name.Length == 0) throw new InvalidOperationException("缺少用户名");
        if (name.Equals("admin", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("管理员不需要分配题库");
        var profile = auth.Get(name);
        if (profile is null)
        {
            // 账号还不在 accounts.dat 里（只在用户数据里）：建一个随机口令的档案，绝不留空口令
            profile = auth.CreateProfile(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)));
            profile["mustChangePassword"] = true;
        }
        if (ids is null) profile.Remove(Field);
        else profile[Field] = ids.Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
        auth.Set(name, profile);
        auth.Save();
    }
}

sealed class QuestionBank
{
    private readonly AppPaths _paths;

    public QuestionBank(AppPaths paths)
    {
        _paths = paths;
    }

    public static void EnsureDatabase(string sqlitePath)
    {
        if (File.Exists(sqlitePath))
        {
            // 旧库补齐列：早期自建的空库缺 ctypscount / brich，会让拉取在
            // BankWriter.CopyMetadataFrom 处抛 "no such column: ctypscount"。
            EnsureBankColumns(sqlitePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath) ?? ".");
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, DefaultTimeout = 30 }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            create table if not exists course (
                icourseid integer primary key,
                ccoursename text,
                ihadbuy integer default 0,
                dchangedate text,
                dchapterchange text,
                dsubjectchange text,
                ctypscount text,
                iclassid integer,
                isubclassid integer,
                iindex integer default 0,
                bstopflag integer default 0
            );
            create table if not exists courseclass (
                iclassid integer primary key,
                ccoursecname text,
                iindex integer default 0
            );
            create table if not exists coursesubclass (
                isubclassid integer primary key,
                csubclassname text,
                iindex integer default 0
            );
            create table if not exists coursechapter (
                ichapterid integer primary key,
                icourseid integer,
                cchaptername text,
                cchaptercode text,
                igrade integer default 0,
                itype integer default 0,
                icount integer default 0,
                bstopflag integer default 0
            );
            create table if not exists coursesubjecttype (
                isubjecttype integer primary key,
                csubjectname text
            );
            create table if not exists coursesubject (
                isubjectid integer primary key,
                icourseid integer,
                ichapterid integer,
                isubjecttype integer,
                iindex integer default 0,
                ichaptertype integer default 0,
                ctitle text,
                cquestion text,
                canswer text,
                cdescription text,
                ianswercount integer default 0,
                iscore text,
                dupdatedate text,
                bstopflag integer default 0,
                brich integer
            );
            insert or ignore into coursesubjecttype(isubjecttype, csubjectname) values
                (0, '单选题'),
                (1, '多选题'),
                (2, '判断题'),
                (3, '问答题'),
                (4, '案例分析'),
                (6, '不定项选择题');
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 补齐旧库缺失的列（幂等）。只在早期"自建空库"上缺这两列；真实导出库本来就有。
    /// 失败只记日志，不阻断启动。
    /// </summary>
    public static void EnsureBankColumns(string sqlitePath)
    {
        if (!File.Exists(sqlitePath)) return;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, DefaultTimeout = 30 }.ToString());
            conn.Open();
            AddColumnIfMissing(conn, "course", "ctypscount", "text");
            AddColumnIfMissing(conn, "coursesubject", "brich", "integer");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 补齐题库列失败（忽略）: {ex.Message}");
        }
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "select count(*) from pragma_table_info($t) where name = $c";
            check.Parameters.AddWithValue("$t", table);
            check.Parameters.AddWithValue("$c", column);
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"alter table \"{table}\" add column \"{column}\" {type}";
        alter.ExecuteNonQuery();
    }

    public IEnumerable<object> GetCourses(string search, bool availableOnly, HashSet<int>? allowed = null,
        IReadOnlyCollection<string>? excludedClasses = null,
        Puller.CourseCatalogSnapshot? upstream = null)
    {
        // 按用户分配的题库范围过滤：null = 不限制；空集合 = 一门都看不到
        var allowedClause = allowed is null
            ? ""
            : allowed.Count == 0
                ? " and 1=0"
                : " and c.icourseid in (" + string.Join(",", allowed) + ")";
        // 非常规分类（默认「合作专区」）整类不展示——那些不是正式题库
        var excludedClause = "";
        if (excludedClasses is { Count: > 0 })
        {
            excludedClause = " and (cl.ccoursecname is null or cl.ccoursecname not in (" +
                             string.Join(",", excludedClasses.Select(n => "'" + n.Replace("'", "''") + "'")) + "))";
        }
        var rows = Query(@"
            select c.icourseid, c.ccoursename, c.ihadbuy, c.dchangedate, c.dchapterchange, c.dsubjectchange,
                   cl.ccoursecname, cl.iindex as class_index, sc.csubclassname, sc.iindex as subclass_index,
                   (select count(*) from coursesubject s where s.icourseid = c.icourseid and coalesce(s.bstopflag, 0)=0) as subject_count
            from course c
            left join courseclass cl on c.iclassid=cl.iclassid
            left join coursesubclass sc on c.isubclassid=sc.isubclassid
            where coalesce(c.bstopflag, 0)=0
              and (@available = 0 or ((select count(*) from coursesubject s where s.icourseid = c.icourseid and coalesce(s.bstopflag, 0)=0) > 0))"
            + allowedClause + excludedClause + @"
              and (@search = '' or c.ccoursename like @like or cl.ccoursecname like @like or sc.csubclassname like @like)
            order by cl.iindex, sc.iindex, c.ihadbuy desc, c.iindex",
            new Dictionary<string, object?> { ["@available"] = availableOnly ? 1 : 0, ["@search"] = search, ["@like"] = "%" + search + "%" });
        var list = rows.Select(r => new
        {
            id = ToInt(r["icourseid"]),
            name = ToStr(r["ccoursename"]),
            category = ToStr(r["ccoursecname"]),
            subcategory = ToStr(r["csubclassname"]),
            // 分类序号：前端按它做「一级 → 二级」分组排序（跟客户端里的 iindex 一致）
            categoryOrder = ToInt(r["class_index"]),
            subcategoryOrder = ToInt(r["subclass_index"]),
            owned = ToInt(r["ihadbuy"]) > 0,
            questionCount = ToInt(r["subject_count"]),
            changedAt = MaxDateString(r["dchangedate"], r["dchapterchange"], r["dsubjectchange"])
        }).ToList();

        /* 管理端列表额外列出「上游有、本地没有」的课程：本地没导入过（course 表里没有），
           或导入了但一门题都没拉下来。这样管理员能看到上游的全部课程、直接点「拉取」，
           不必再靠猜 ID。课程名/分类/序号取自上游课程目录（CourseCatalog）。
           学员端（availableOnly=true）**不加**：这些课本地没有正文，加了也点不开。 */
        if (upstream is not null && !availableOnly)
        {
            var localIds = list.Select(c => c.id).ToHashSet();
            var classNames = upstream.Classes.ToDictionary(c => c.Id, c => c.Name ?? "");
            var classIndexes = upstream.Classes.ToDictionary(c => c.Id, c => c.Index ?? 0);
            var subNames = upstream.SubClasses.ToDictionary(c => c.Id, c => c.Name ?? "");
            var subIndexes = upstream.SubClasses.ToDictionary(c => c.Id, c => c.Index ?? 0);
            var excluded = excludedClasses is { Count: > 0 }
                ? new HashSet<string>(excludedClasses, StringComparer.Ordinal)
                : null;
            var needle = search.Trim().ToLowerInvariant();
            foreach (var course in upstream.Courses)
            {
                if (course.Stopped) continue;
                if (localIds.Contains((int)course.Id)) continue;
                if (allowed is not null && !allowed.Contains((int)course.Id)) continue;
                var className = course.ClassId is { } cid && classNames.TryGetValue(cid, out var cn) ? cn : "";
                var subName = course.SubClassId is { } sid && subNames.TryGetValue(sid, out var sn) ? sn : "";
                // 与本地课程同一套排除规则：非常规分类（默认「合作专区」）不展示
                if (excluded is not null && className.Length > 0 && excluded.Contains(className)) continue;
                var courseName = course.Name ?? "";
                if (needle.Length > 0
                    && !courseName.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                    && !className.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                    && !subName.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                    && !course.Id.ToString(CultureInfo.InvariantCulture).Contains(needle, StringComparison.Ordinal)) continue;
                list.Add(new
                {
                    id = (int)course.Id,
                    name = courseName,
                    category = className,
                    subcategory = subName,
                    categoryOrder = course.ClassId is { } c2 && classIndexes.TryGetValue(c2, out var ci) ? ci : 0,
                    subcategoryOrder = course.SubClassId is { } s2 && subIndexes.TryGetValue(s2, out var si) ? si : 0,
                    owned = false,
                    questionCount = 0,
                    // MaxDateString 里是 Convert.ToDateTime，空串会抛；先滤成 null
                    changedAt = MaxDateString(
                        NonEmpty(course.ChangedDate), NonEmpty(course.ChapterChange), NonEmpty(course.SubjectChange))
                });
            }
        }
        return list;

        static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>这道题属于哪门课（用于按用户分配题库的越权校验；找不到返回 0）。</summary>
    public int CourseIdOfSubject(int subjectId)
    {
        var row = Query("select icourseid from coursesubject where isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = subjectId }).FirstOrDefault();
        return row is null ? 0 : ToInt(row["icourseid"]);
    }

    /// <summary>这门课在库里的章节/题目增量水位（没有则 null）——用于"可更新"探测。</summary>
    public (string? Chapter, string? Subject) CourseWatermarks(int courseId)
    {
        var row = Query("select dchapterchange, dsubjectchange from course where icourseid=@id",
            new Dictionary<string, object?> { ["@id"] = courseId }).FirstOrDefault();
        if (row is null) return (null, null);
        string? Clean(object? value)
        {
            var text = ToStr(value);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return (Clean(row["dchapterchange"]), Clean(row["dsubjectchange"]));
    }

    /// <summary>
    /// 健康探针：跑真实读查询（表/列齐全才算健康），不返回规模明细。
    /// 以前 health 只判"文件存在"，题库坏掉时 /api/courses 500 而 health 仍 200。
    /// </summary>
    public static bool ProbeHealthy(string sqlitePath)
    {
        if (!File.Exists(sqlitePath)) return false;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 5,
            }.ToString());
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select count(*) from course c left join courseclass cl on c.iclassid=cl.iclassid";
                cmd.ExecuteScalar();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select count(*) from coursesubject s left join coursesubjecttype t on s.isubjecttype=t.isubjecttype";
                cmd.ExecuteScalar();
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] health probe failed: {ex.Message}");
            return false;
        }
    }

    public static object TryReadTableCounts(string sqlitePath)
    {
        if (!File.Exists(sqlitePath)) return new { ok = false };
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 30 }.ToString());
            conn.Open();
            var result = new Dictionary<string, int>();
            foreach (var table in new[] { "course", "coursechapter", "coursesubject", "coursesubjecttype" })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "select count(*) from " + table;
                result[table] = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {ex}");
            return new { ok = false };
        }
    }

    public IEnumerable<object> GetChapters(int courseId)
    {
        /* 这里**故意不选** ch.icount：它是死列（脏数据），与实算普遍对不上——
           课程 20 的 213 个章节里 104 个不一致（sum(icount)=23252 vs sum(实算)=9155，
           例：章节 570671 的 icount=15 而实算 21）；全库 773 个章节里 279 个不一致
           （sum(icount)=75551 vs sum(实算)=29738）。
           questionCount 一律用下面的 subject_count 子查询实算；别再「顺手」把 icount 加回来。 */
        var rows = Query(@"
            select ch.ichapterid, ch.cchaptername, ch.cchaptercode, ch.igrade, ch.itype,
                   (select count(*) from coursesubject s where s.ichapterid=ch.ichapterid and coalesce(s.bstopflag, 0)=0) as subject_count
            from coursechapter ch
            where ch.icourseid=@courseId and coalesce(ch.bstopflag, 0)=0
            order by ch.itype, ch.cchaptercode",
            new Dictionary<string, object?> { ["@courseId"] = courseId });
        return rows.Select(r => new
        {
            id = ToInt(r["ichapterid"]),
            name = ToStr(r["cchaptername"]),
            code = ToStr(r["cchaptercode"]),
            grade = ToInt(r["igrade"]),
            type = ToInt(r["itype"]),
            questionCount = ToInt(r["subject_count"])
        }).ToList();
    }

    public IEnumerable<object> GetTypes(int courseId, int? chapterId, List<int> chapterIds)
    {
        var parameters = new Dictionary<string, object?> { ["@courseId"] = courseId };
        var sql = @"
            select s.isubjecttype, t.csubjectname, count(*) as subject_count
            from coursesubject s
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            where s.icourseid=@courseId and coalesce(s.bstopflag, 0)=0";
        if (chapterId.HasValue)
        {
            sql += " and s.ichapterid=@chapterId";
            parameters["@chapterId"] = chapterId.Value;
        }
        if (chapterIds.Count > 0) sql += " and s.ichapterid in (" + string.Join(",", chapterIds) + ")";
        sql += " group by s.isubjecttype, t.csubjectname order by s.isubjecttype";
        var rows = Query(sql, parameters);
        return rows.Select(r => new
        {
            id = ToInt(r["isubjecttype"]),
            name = ToStr(r["csubjectname"]),
            questionCount = ToInt(r["subject_count"])
        }).ToList();
    }

    public IEnumerable<object> GetQuestions(int courseId, int? chapterId, List<int> chapterIds, int? typeId, string search, string order, int? limit, List<int> ids)
    {
        var effectiveLimit = Math.Max(1, Math.Min(30000, limit ?? 120));
        var queryLimit = search.Length > 0 ? 30000 : effectiveLimit;
        if (ids.Count > 0) queryLimit = Math.Min(30000, Math.Max(queryLimit, ids.Count));
        if (chapterIds.Count > 0) queryLimit = search.Length > 0 ? queryLimit : Math.Min(30000, Math.Max(queryLimit, effectiveLimit));

        var parameters = new Dictionary<string, object?> { ["@courseId"] = courseId };
        var sql = @"
            select s.isubjectid, s.icourseid, s.ichapterid, s.isubjecttype, s.iindex, s.iscore,
                   s.ctitle, s.ianswercount, s.dupdatedate, t.csubjectname, ch.cchaptername
            from coursesubject s
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            left join coursechapter ch on s.ichapterid=ch.ichapterid
            where s.icourseid=@courseId and coalesce(s.bstopflag, 0)=0";
        if (chapterId.HasValue)
        {
            sql += " and s.ichapterid=@chapterId";
            parameters["@chapterId"] = chapterId.Value;
        }
        if (chapterIds.Count > 0) sql += " and s.ichapterid in (" + string.Join(",", chapterIds) + ")";
        if (typeId.HasValue)
        {
            sql += " and s.isubjecttype=@typeId";
            parameters["@typeId"] = typeId.Value;
        }
        if (ids.Count > 0) sql += " and s.isubjectid in (" + string.Join(",", ids) + ")";
        sql += order == "random"
            ? " order by random()"
            : " order by s.ichaptertype, ch.cchaptercode, s.iindex, s.isubjectid";
        sql += " limit " + queryLimit;
        var rows = Query(sql, parameters);
        var items = rows.Select(r => new
        {
            id = ToInt(r["isubjectid"]),
            chapterId = ToInt(r["ichapterid"]),
            chapterName = ToStr(r["cchaptername"]),
            type = ToStr(r["csubjectname"]),
            subjectType = ToInt(r["isubjecttype"]),
            index = ToInt(r["iindex"]),
            /* 卷面分值。题库里 100% 的题都有（实测 29738/29738），
               且和真题卷面完全吻合：造价管理真题 60×1 + 20×2 = 100 分。
               前端组卷算分要用，以前这个接口没带，只能拿题型写死的值。 */
            score = ToStr(r["iscore"]),
            title = StripHtml(DecryptField(r["ctitle"], r["dupdatedate"])),
            answerCount = ToInt(r["ianswercount"])
        });
        if (search.Length > 0) items = items.Where(i => i.title.Contains(search, StringComparison.OrdinalIgnoreCase));
        return items.Take(effectiveLimit).ToList();
    }

    /// <summary>
    /// 整门课的「题目 id → 章节 id」映射，扁平数组 [qid, cid, qid, cid, ...]。
    /// 前端拿它补章节树的「已做」统计：本地只记了做过的题目 id，
    /// 不知道每题属于哪一章，翻一遍题目又太慢。
    /// </summary>
    public object GetQuestionChapterPairs(int courseId)
    {
        var rows = Query(@"
            select isubjectid, ichapterid from coursesubject
            where icourseid=@courseId and coalesce(bstopflag, 0)=0",
            new Dictionary<string, object?> { ["@courseId"] = courseId });
        var flat = new List<int>(rows.Count * 2);
        foreach (var r in rows)
        {
            flat.Add(ToInt(r["isubjectid"]));
            flat.Add(ToInt(r["ichapterid"]));
        }
        return new { ok = true, courseId, count = rows.Count, pairs = flat };
    }

    public object? GetQuestion(int id)
    {
        var rows = Query(@"
            select s.*, t.csubjectname, ch.cchaptername
            from coursesubject s
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            left join coursechapter ch on s.ichapterid=ch.ichapterid
            where s.isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id });
        if (rows.Count == 0) return null;
        var r = rows[0];
        var title = NormalizeHtml(DecryptField(r["ctitle"], r["dupdatedate"]));
        var question = NormalizeHtml(DecryptField(r["cquestion"], r["dupdatedate"]));
        var answer = StripHtml(DecryptField(r["canswer"], r["dupdatedate"])).Trim();
        var description = NormalizeHtml(DecryptField(r["cdescription"], r["dupdatedate"]));
        var subjectType = ToInt(r["isubjecttype"]);
        var answerCount = ToInt(r["ianswercount"]);
        /* 题型自己就说明了「这道题有没有选项」，不能靠正则去题干里猜。
           老实现对所有题型一律跑 ExtractOptions，两种题因此坏掉：
             · 判断题(2)：库里根本没有选项（canswer 是「对」/「错」）→ options 恒为空 →
               前端按「没有 options 就是主观题」渲染成填空框 → 没有对/错按钮、
               不计分、不进错题本（实测 23657642，markResult 调用 0 次）。
             · 问答题(3)/解析题(4)/填空题(5)：答案是空串，题干里的「1. 2.」这类编号
               被误当成选项 → course 24 有 21 道问答题判成客观题，答什么都是错、
               还进错题本（实测 24082359 被解析出 5 个假选项 A/B/C/D/A）。 */
        List<object> options;
        var stem = title;
        if (subjectType is 3 or 4 or 5)
        {
            options = new List<object>();        // 主观题：一律没有选项，保持 stem/extraQuestion 原样
        }
        else if (subjectType == 2)
        {
            // text 留空：前端选项按钮画成「<b>标签</b><span>正文</span>」，判断题不需要正文
            options = new List<object> { new { label = "对", text = "" }, new { label = "错", text = "" } };
            // 判断题的 canswer 就是「对」/「错」，与合成选项的 label 同口径 → 判分/错题本/答题卡全通
        }
        else
        {
            options = ExtractOptions(title, answerCount);
            stem = options.Count > 0 ? RemoveOptions(title) : title;
            if (options.Count == 0 && question.Length > 0)
            {
                options = ExtractOptions(question, answerCount);
                if (options.Count > 0) question = "";
            }
        }
        return new
        {
            id = ToInt(r["isubjectid"]),
            courseId = ToInt(r["icourseid"]),
            chapterId = ToInt(r["ichapterid"]),
            chapterName = ToStr(r["cchaptername"]),
            type = ToStr(r["csubjectname"]),
            subjectType,
            score = ToStr(r["iscore"]),
            stem,
            extraQuestion = question,
            answer,
            description,
            options,
            updatedAt = ToDateString(r["dupdatedate"])
        };
    }

    public object GetEditorQuestions(int courseId, int? chapterId, List<int> chapterIds, string search, List<int> ids, int? limit)
    {
        var take = Math.Max(20, Math.Min(500, limit ?? 120));
        var queryLimit = ids.Count > 0 ? Math.Min(5000, Math.Max(take, ids.Count)) : search.Trim().Length > 0 ? 5000 : take;
        var parameters = new Dictionary<string, object?>();
        var sql = @"
            select s.isubjectid, s.icourseid, s.ichapterid, s.isubjecttype, s.iindex, s.ctitle, s.cquestion, s.canswer,
                   s.cdescription, s.ianswercount, s.dupdatedate, t.csubjectname, ch.cchaptername, ch.cchaptercode, ch.igrade
            from coursesubject s
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            left join coursechapter ch on s.ichapterid=ch.ichapterid
            where coalesce(s.bstopflag, 0)=0";
        if (courseId > 0)
        {
            sql += " and s.icourseid=@courseId";
            parameters["@courseId"] = courseId;
        }
        if (chapterId.HasValue && chapterId.Value > 0)
        {
            sql += " and s.ichapterid=@chapterId";
            parameters["@chapterId"] = chapterId.Value;
        }
        if (chapterIds.Count > 0) sql += " and s.ichapterid in (" + string.Join(",", chapterIds) + ")";
        if (ids.Count > 0) sql += " and s.isubjectid in (" + string.Join(",", ids) + ")";
        sql += " order by s.ichaptertype, ch.cchaptercode, s.iindex, s.isubjectid limit " + queryLimit.ToString(CultureInfo.InvariantCulture);

        var keyword = search.Trim();
        // 本地改动叠层里有记录的题，在列表里标「本地已改」（按课程缓存，避免逐题读文件）
        var editCache = new Dictionary<int, HashSet<int>>();
        string? localEditsError = null;
        bool HasLocalEdit(int cid, int sid)
        {
            if (cid <= 0) return false;
            if (!editCache.TryGetValue(cid, out var set))
            {
                try
                {
                    set = Puller.LocalEdits.EditedSubjectIds(_paths.SqlitePath, cid);
                }
                catch (Puller.LocalEditsUnavailableException ex)
                {
                    /* 叠层读不了：**不能**把"读不到"显示成"没有本地改动"
                       （否则管理员会以为改动丢了、或对着一份不完整的列表操作）。
                       列表照常返回，但把错误一并带给界面（localEditsError）。 */
                    localEditsError ??= SafeErrorText(ex);
                    set = new HashSet<int>();
                }
                editCache[cid] = set;
            }
            return set.Contains(sid);
        }
        var rows = Query(sql, parameters)
            .Select(r =>
            {
                var title = TextForEdit(r["ctitle"], r["dupdatedate"]);
                var question = TextForEdit(r["cquestion"], r["dupdatedate"]);
                var answer = TextForEdit(r["canswer"], r["dupdatedate"]);
                var description = TextForEdit(r["cdescription"], r["dupdatedate"]);
                return new
                {
                    id = ToInt(r["isubjectid"]),
                    courseId = ToInt(r["icourseid"]),
                    chapterId = ToInt(r["ichapterid"]),
                    chapterName = ToStr(r["cchaptername"]),
                    chapterCode = ToStr(r["cchaptercode"]),
                    chapterGrade = ToInt(r["igrade"]),
                    type = ToStr(r["csubjectname"]),
                    subjectType = ToInt(r["isubjecttype"]),
                    index = ToInt(r["iindex"]),
                    title = StripHtml(title),
                    rawTitle = title,
                    rawQuestion = question,
                    rawAnswer = answer,
                    rawDescription = description,
                    answer = StripHtml(answer),
                    answerCount = ToInt(r["ianswercount"]),
                    localEdited = HasLocalEdit(ToInt(r["icourseid"]), ToInt(r["isubjectid"]))
                };
            });
        if (keyword.Length > 0)
        {
            rows = rows.Where(item =>
                item.id.ToString(CultureInfo.InvariantCulture).Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.rawQuestion.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.rawAnswer.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.rawDescription.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.chapterName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        var filtered = rows.Take(take).ToList();
        return new { ok = true, items = filtered, count = filtered.Count, limit = take, localEditsError };
    }

    public object? GetEditorQuestion(int id)
    {
        var rows = Query(@"
            select s.*, c.ccoursename, t.csubjectname, ch.cchaptername, ch.cchaptercode, ch.igrade
            from coursesubject s
            left join course c on s.icourseid=c.icourseid
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            left join coursechapter ch on s.ichapterid=ch.ichapterid
            where s.isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id });
        if (rows.Count == 0) return null;
        var r = rows[0];
        var courseId = ToInt(r["icourseid"]);
        var chapterId = ToInt(r["ichapterid"]);
        /* 选项体检：口径与读侧同一份 ParseOptions —— 重复 / 非顺序标签、答案不在选项里。
           只对客观题（单选 0 / 多选 1 / 不定项 6）算；判断题的「对/错」和主观题本来就没有选项，
           拿选项规则去套它们只会报假警。 */
        var subjectType = ToInt(r["isubjecttype"]);
        var localEditError = (string?)null;
        var localEditMark = DescribeLocalEdit(courseId, id, out localEditError);
        var optionWarnings = subjectType is 0 or 1 or 6
            ? AuditOptions(TextForEdit(r["ctitle"], r["dupdatedate"]),
                           TextForEdit(r["cquestion"], r["dupdatedate"]),
                           TextForEdit(r["canswer"], r["dupdatedate"]),
                           ToInt(r["ianswercount"]))
            : new List<string>();
        return new
        {
            ok = true,
            id = ToInt(r["isubjectid"]),
            courseId,
            courseName = ToStr(r["ccoursename"]),
            chapterId,
            chapterName = ToStr(r["cchaptername"]),
            chapterCode = ToStr(r["cchaptercode"]),
            chapterGrade = ToInt(r["igrade"]),
            chapterPath = GetChapterPath(courseId, chapterId),
            type = ToStr(r["csubjectname"]),
            subjectType,
            title = TextForEdit(r["ctitle"], r["dupdatedate"]),
            question = TextForEdit(r["cquestion"], r["dupdatedate"]),
            answer = TextForEdit(r["canswer"], r["dupdatedate"]),
            description = TextForEdit(r["cdescription"], r["dupdatedate"]),
            answerCount = ToInt(r["ianswercount"]),
            optionWarnings,
            score = ToStr(r["iscore"]),
            updatedAt = ToDateString(r["dupdatedate"]),
            // 本地改动叠层里有记录 → 界面标「本地已改」并提供一键还原。
            // 叠层读不了 → localEdit 为 null 但**带上错误**，不伪装成"没有本地改动"。
            localEdit = localEditMark,
            localEditError
        };
    }

    /// <summary>
    /// 读"这道题有没有本地改动"。读失败不吞：返回 null 并把错误写进 <paramref name="error"/>，
    /// 由接口随响应带出去（<see cref="Puller.LocalEdits.DescribeSubject"/> 本身会抛）。
    /// </summary>
    private object? DescribeLocalEdit(int courseId, int subjectId, out string? error)
    {
        error = null;
        try
        {
            return Puller.LocalEdits.DescribeSubject(_paths.SqlitePath, courseId, subjectId);
        }
        catch (Puller.LocalEditsUnavailableException ex)
        {
            error = SafeErrorText(ex);
            return null;
        }
    }

    public object SaveEditorQuestion(Dictionary<string, object?> body, string? editor = null)
    {
        /* ⚠️ 全链路进写闸（BE-06）：编辑要先读 current / before / 章节路径（好几次 Query），
           再备份、事务写库，最后另写 JSON 叠层。旧实现没有 Enter，与拉取/整包导入交错时：
           读到的是旧库、写进的是刚换过的新库（可能改错归属）、写入 0 行也返回 ok、
           叠层顺序与库相反。SQLite 只串行物理写入，替代不了应用层互斥。
           锁顺序固定：BankWriteGate → LocalEdits.Gate（后者只在 LocalEdits 内部取，
           它不会反过来取 BankWriteGate，所以不会死锁）。 */
        using var gate = BankWriteGate.Enter();
        var id = BodyInt(body, "id");
        if (id <= 0) throw new InvalidOperationException("缺少题目 ID");
        var current = Query("select isubjectid, icourseid, ichapterid, dupdatedate from coursesubject where isubjectid=@id", new Dictionary<string, object?> { ["@id"] = id }).FirstOrDefault();
        if (current is null) throw new InvalidOperationException("题目不存在");

        /* 乐观并发（可选）：客户端带上打开这道题时读到的版本号（GetEditorQuestion 返回的
           updatedAt），与库里当前值不一致说明别人已经改过 → 拒绝，别把别人的修改盖掉。
           两位管理员同题保存没有 revision 时，至少闸保证两者串行（后写的整条覆盖前一条，
           叠层里"服务端快照"仍是最早那份，一键还原不会回到中间态）。 */
        var revision = GetBodyString(body, "revision");
        if (revision.Length > 0)
        {
            var actualRevision = ToDateString(current["dupdatedate"]);
            if (!RevisionMatches(revision, actualRevision))
            {
                throw new InvalidOperationException(
                    $"这道题在你打开之后已经被改过（库里当前版本 {actualRevision}，你手里是 {revision}）；请刷新后重新修改，避免覆盖别人的改动");
            }
        }

        var title = NormalizeEditText(GetBodyString(body, "title"));
        var question = NormalizeEditText(GetBodyString(body, "question"));
        var answer = NormalizeEditText(GetBodyString(body, "answer"));
        var description = NormalizeEditText(GetBodyString(body, "description"));
        if (title.Length == 0) throw new InvalidOperationException("题目内容不能为空");

        var courseId = ToInt(current["icourseid"]);
        var chaptersToRename = BodyChapters(body).Where(c => c.Id > 0 && c.Name.Length > 0).ToList();
        /* ⚠️ chapters[].id 以前**完全不校验**：传 {id:1} 也照收，于是能一次改掉任意
           **另一章**的名字（实测传路径外章节 570708，返回 200 且该章名被改）。
           只允许改这道题自己 chapterPath 上的章节 —— 判定与界面渲染走同一份 ChapterPathRows。 */
        if (chaptersToRename.Count > 0)
        {
            var allowedChapters = ChapterPathIds(courseId, ToInt(current["ichapterid"]));
            var illegal = chaptersToRename.Where(c => !allowedChapters.Contains(c.Id)).Select(c => c.Id).ToList();
            if (illegal.Count > 0)
            {
                throw new InvalidOperationException(
                    $"章节 {string.Join("、", illegal)} 不属于这道题的章节路径，已拒绝改名（只能改这道题所在的章）");
            }
        }
        // 记下"改动前"的内容（原样存，供一键还原用）
        var before = Query("select ctitle, cquestion, canswer, cdescription from coursesubject where isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id }).FirstOrDefault();
        var chapterNamesBefore = Query(
            "select ichapterid, cchaptername from coursechapter where ichapterid in (" +
            string.Join(",", chaptersToRename.Select(c => c.Id)) + ")",
            new Dictionary<string, object?>())
            .ToDictionary(r => ToInt(r["ichapterid"]), r => ToStr(r["cchaptername"]));

        /* 这道题的内容在主库还是拉取库？本地改动要写回它所在的那一份——遮蔽视图本身不可写。
           ⚠️ 备份的 owner 与"写库的 owner"必须是**同一份判定**（同一个连接、同一句
           select count(*) from main.coursesubject）。两条路径分别判定会出现
           "库写进了拉取库、备份却备份了主库"，那样备份里根本没有这道题的原数据。 */
        SqliteConnection.ClearAllPools();
        using var conn = OpenBank(_paths.SqlitePath, _paths.PulledSqlitePath, readOnly: false);
        var owner = MainHoldsCourse(conn, courseId) ? "main" : "pulled";
        var ownerPath = owner == "main" ? _paths.SqlitePath : _paths.PulledSqlitePath;
        // 备份**即将被改的那一份**库 + 唯一名字（旧实现永远只备份主库；题在拉取库里时
        // 备份里根本没有这道题的原数据，返回的 backup 路径不可用于恢复这次操作）
        var backupDir = BackupQuestionBank("edit-question-" + id.ToString(CultureInfo.InvariantCulture), ownerPath);

        var savedAt = NowText();
        /* ── 顺序：**先把叠层写好，再写库** ─────────────────────────────────────
           叠层写不了（权限/磁盘/被占用）→ 在动库之前抛：库保持原样、旧叠层文件完好
           （Save 走同目录临时文件 + rename），接口明确报错；不会出现旧实现那种
           "库改了、记录没写上、接口还返回 ok"。
           写库失败 → 把叠层原文写回快照，状态仍然自洽（库与记录都还是改动前）。 */
        var layerSnapshot = Puller.LocalEdits.Snapshot(_paths.SqlitePath);
        var layerTouched = false;
        try
        {
            // 记进本地改动叠层：拉取/重新导出/导入题库包后按题号贴回（本地优先），界面可一键还原
            Puller.LocalEdits.RememberSubject(_paths.SqlitePath, courseId, id,
                new Puller.LocalSubjectEdit
                {
                    Title = title,
                    Question = question,
                    Answer = answer,
                    Description = description,
                    EditedAt = savedAt,
                    Editor = editor ?? "",
                },
                before is null ? null : new Puller.LocalSubjectEdit
                {
                    Title = ToStr(before["ctitle"]),
                    Question = ToStr(before["cquestion"]),
                    Answer = ToStr(before["canswer"]),
                    Description = ToStr(before["cdescription"]),
                });
            layerTouched = true;
            foreach (var chapter in chaptersToRename)
            {
                Puller.LocalEdits.RememberChapter(_paths.SqlitePath, courseId, chapter.Id, chapter.Name,
                    chapterNamesBefore.TryGetValue(chapter.Id, out var was) ? was : null, editor);
            }

            // 复用上面那个连接与 owner 判定：备份的库 = 这次真正会写的那一份
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var chapter in BodyChapters(body))
                {
                    if (chapter.Id <= 0 || chapter.Name.Length == 0) continue;
                    using var chapterCmd = conn.CreateCommand();
                    chapterCmd.Transaction = tx;
                    chapterCmd.CommandText = $"update {owner}.coursechapter set cchaptername=@name where ichapterid=@id";
                    chapterCmd.Parameters.AddWithValue("@name", chapter.Name);
                    chapterCmd.Parameters.AddWithValue("@id", chapter.Id);
                    // 0 行 = 章节不在了却什么都没改，别当成成功
                    if (chapterCmd.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException($"章节 {chapter.Id} 的改名没有生效（章节可能已被删除），请刷新后重试");
                    }
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $@"
                    update {owner}.coursesubject
                    set ctitle=@title,
                        cquestion=@question,
                        canswer=@answer,
                        cdescription=@description,
                        ianswercount=@answerCount,
                        dupdatedate=@updatedAt
                    where isubjectid=@id";
                cmd.Parameters.AddWithValue("@title", title);
                cmd.Parameters.AddWithValue("@question", question);
                cmd.Parameters.AddWithValue("@answer", answer);
                cmd.Parameters.AddWithValue("@description", description);
                cmd.Parameters.AddWithValue("@answerCount", CountOptions(title + "\n" + question));
                cmd.Parameters.AddWithValue("@updatedAt", savedAt);
                cmd.Parameters.AddWithValue("@id", id);
                var rows = cmd.ExecuteNonQuery();
                // 0 行 = 什么都没改却返回"保存成功"（旧实现正是这样）；!= 1 一律当失败
                if (rows != 1)
                {
                    throw new InvalidOperationException($"题目更新没有生效（影响 {rows} 行），题目可能已被删除，请刷新后重试");
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }
        catch
        {
            // 库那一步没成 → 叠层退回保存前的原文（库里还是旧内容，记录也不该留下新内容）
            if (layerTouched)
            {
                try
                {
                    Puller.LocalEdits.RestoreSnapshot(_paths.SqlitePath, layerSnapshot);
                }
                catch (Exception restoreEx)
                {
                    Console.WriteLine($"[editor-save-layer-rollback-failed] subject={id} {restoreEx.Message}");
                }
            }
            throw;
        }

        return new
        {
            ok = true,
            id,
            backup = backupDir,
            backupOf = Path.GetFileName(ownerPath),
            savedAt,
            revision = savedAt,   // 下一次保存时带回来做乐观并发校验
            question = GetEditorQuestion(id)
        };
    }

    /// <summary>把本地改过的题还原成服务端版本（叠层里记的那一份），并删掉叠层记录。</summary>
    public object RestoreEditorQuestion(int id)
    {
        // 与保存同一把闸：还原要"读叠层 → 写库 → 重算计数（第二次写库）"，
        // 与拉取/导入交错会删掉还需要的记录，或让库与叠层的顺序相反（BE-06）
        using var gate = BankWriteGate.Enter();
        if (id <= 0) throw new InvalidOperationException("缺少题目 ID");
        var row = Query("select isubjectid, icourseid from coursesubject where isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id }).FirstOrDefault();
        if (row is null) throw new InvalidOperationException("题目不存在");
        var courseId = ToInt(row["icourseid"]);
        /* 还原写哪一份库、就备份哪一份（与 <see cref="RestoreSubject"/> 的目标、
           以及下面重算计数的 owner 用**同一份判定**：同一个连接 + main. 限定）。 */
        SqliteConnection.ClearAllPools();
        string owner;
        using (var conn = OpenBank(_paths.SqlitePath, _paths.PulledSqlitePath, readOnly: false))
        {
            owner = MainHoldsCourse(conn, courseId) ? "main" : "pulled";
        }
        var target = owner == "main" ? _paths.SqlitePath : _paths.PulledSqlitePath;
        // 还原会删掉叠层记录（= 丢掉"再改回去"的依据），先备份**实际会被写的那一份**库
        var backupDir = BackupQuestionBank("restore-question-" + id.ToString(CultureInfo.InvariantCulture), target);
        // 叠层读/写失败会抛（不再静默）：接口明确失败，不会出现"库还原了、记录还在"或反之
        var ok = Puller.LocalEdits.RestoreSubject(_paths.SqlitePath, target, courseId, id, out var message);
        /* ⚠️ 还原必须连 ianswercount 一起回，否则「还原 ≠ 恢复」：
           RestoreSubject 只写回 ctitle/cquestion/canswer/cdescription 四个字段，
           而 ianswercount 停在「删过选项」的那次保存上。实测 1509187 删掉 C 行保存
           （4→3）再还原：题干回到 A/B/C/D、答案回到 B，可 ianswercount 仍是 3 →
           学员端 options 被截成 A/B/C，D 消失。
           这里按**和保存路径同一口径**（CountOptions）从还原后的文本重算。 */
        if (ok) RecomputeAnswerCount(id, courseId, owner);
        return new { ok, id, message, backup = backupDir, backupOf = Path.GetFileName(target), question = ok ? GetEditorQuestion(id) : null };
    }

    /// <summary>
    /// 按保存时的同一口径重算这道题的 <c>ianswercount</c>。
    /// 保存路径写的是 <see cref="CountOptions"/>(title + "\n" + question)，回填必须同口径，
    /// 否则会出现「还原一次 / 保存一次就来回变」的抖动。
    /// <paramref name="owner"/>（"main"/"pulled"）由调用方判定 —— 与还原写库的目标必须是同一份。
    /// </summary>
    private void RecomputeAnswerCount(int id, int courseId, string owner)
    {
        var row = Query("select ctitle, cquestion, dupdatedate from coursesubject where isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id }).FirstOrDefault();
        if (row is null) throw new InvalidOperationException("题目已不在题库里（可能已被上游下线）");
        var count = CountOptions(TextForEdit(row["ctitle"], row["dupdatedate"]) + "\n" + TextForEdit(row["cquestion"], row["dupdatedate"]));
        SqliteConnection.ClearAllPools();
        using var conn = OpenBank(_paths.SqlitePath, _paths.PulledSqlitePath, readOnly: false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"update {owner}.coursesubject set ianswercount=@count where isubjectid=@id";
        cmd.Parameters.AddWithValue("@count", count);
        cmd.Parameters.AddWithValue("@id", id);
        var rows = cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
        // 0 行 = 计数没回填却返回"还原成功"（旧实现如此），学员端下次仍会把选项截短
        if (rows != 1) throw new InvalidOperationException($"还原后重算选项数没有生效（影响 {rows} 行），请刷新后重试");
    }

    /// <summary>这门课的内容在主库（导出产物）还是在拉取库（未购课程）。</summary>
    private static bool MainHoldsCourse(SqliteConnection conn, int courseId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from main.coursesubject where icourseid=@c";
        cmd.Parameters.AddWithValue("@c", courseId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private List<object> GetChapterPath(int courseId, int chapterId)
    {
        return ChapterPathRows(courseId, chapterId)
            .Select(row => (object)new
            {
                id = row.Id,
                name = row.Name,
                code = row.Code,
                grade = row.Grade,
                questionCount = row.QuestionCount
            })
            .ToList();
    }

    /// <summary>
    /// 章节路径的原始行。<see cref="GetChapterPath"/>（给界面渲染章节输入框）与
    /// <see cref="SaveEditorQuestion"/>（校验改名的章节 id 是不是这道题自己的章）
    /// 必须走同一份判定，否则「界面能显示、后端拒绝」或反过来「后端放行越权改别人的章」。
    /// </summary>
    private List<(int Id, string Name, string Code, int Grade, int QuestionCount)> ChapterPathRows(int courseId, int chapterId)
    {
        if (courseId <= 0 || chapterId <= 0) return new List<(int Id, string Name, string Code, int Grade, int QuestionCount)>();
        var chapters = Query(@"
            select ch.ichapterid, ch.cchaptername, ch.cchaptercode, ch.igrade, ch.itype,
                   (select count(*) from coursesubject s where s.ichapterid=ch.ichapterid and coalesce(s.bstopflag,0)=0) as subject_count
            from coursechapter ch
            where ch.icourseid=@courseId and coalesce(ch.bstopflag,0)=0
            order by ch.itype, ch.cchaptercode",
            new Dictionary<string, object?> { ["@courseId"] = courseId });
        var target = chapters.FirstOrDefault(row => ToInt(row["ichapterid"]) == chapterId);
        if (target is null) return new List<(int Id, string Name, string Code, int Grade, int QuestionCount)>();
        var code = ToStr(target["cchaptercode"]);
        var type = ToInt(target["itype"]);
        return chapters
            .Where(row => ToInt(row["itype"]) == type && code.StartsWith(ToStr(row["cchaptercode"]), StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => ToInt(row["igrade"]))
            .ThenBy(row => ToStr(row["cchaptercode"]).Length)
            .Select(row => (ToInt(row["ichapterid"]), ToStr(row["cchaptername"]), ToStr(row["cchaptercode"]), ToInt(row["igrade"]), ToInt(row["subject_count"])))
            .ToList();
    }

    /// <summary>这道题所在章节路径上的章节 id 集合 —— 改章节名只允许动这些章。</summary>
    private HashSet<int> ChapterPathIds(int courseId, int chapterId)
        => ChapterPathRows(courseId, chapterId).Select(row => row.Id).ToHashSet();

    /// <summary>
    /// 备份**即将被修改的那一份**库：题在主库就备份主库，题在拉取库就备份拉取库
    /// （旧实现永远只备份主库 —— 题写进拉取库时返回的 backup 里没有这道题的原数据，
    /// 等于"给了个恢复不了这次操作的备份"）。
    /// 用 SQLite 在线备份 API 取一致快照；名字用 <see cref="TimestampUnique"/>
    /// （秒级时间戳在同一秒内连存两次会覆盖同一份快照）。
    /// </summary>
    private string BackupQuestionBank(string reason, string ownerDbPath)
    {
        var owner = Path.GetFullPath(ownerDbPath);
        if (!File.Exists(owner)) throw new FileNotFoundException("SQLite question bank not found", owner);
        var dataRoot = Directory.GetParent(_paths.SqlitePath)?.FullName ?? _paths.BaseRoot;
        // 备份目录名里带上"备份的是哪一份库"，人工恢复时不会拿错（bank / bank-pulled）
        var ownerTag = Path.GetFileNameWithoutExtension(owner).Replace("question-bank", "bank", StringComparison.OrdinalIgnoreCase);
        var backupDir = Path.Combine(dataRoot, "_backups", CleanBackupName(reason) + "-" + ownerTag + "-" + TimestampUnique());
        Directory.CreateDirectory(backupDir);
        SqliteSnapshot.Copy(owner, Path.Combine(backupDir, Path.GetFileName(owner)));
        BackupRetention.Prune(Path.Combine(dataRoot, "_backups"));
        return backupDir;
    }

    private static string CleanBackupName(string value)
    {
        value = Regex.Replace(value, @"[^\w\-]+", "-").Trim('-');
        return value.Length == 0 ? "edit" : value.Length > 80 ? value[..80] : value;
    }

    /// <summary>
    /// 版本号（<c>dupdatedate</c>）比对，给保存路径做乐观并发用。
    /// 两边先按读路径同一口径（<see cref="ToDateString"/>）比字符串，再退一步按时间解析比 ——
    /// 客户端可能回传 "2026-07-02 15:10:02" / 带 T / 带毫秒的写法。
    /// </summary>
    private static bool RevisionMatches(string expected, string actual)
    {
        var left = expected.Trim();
        var right = actual.Trim();
        if (left == right) return true;
        return DateTime.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
               && DateTime.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b)
               && a == b;
    }

    private static int BodyInt(Dictionary<string, object?> body, string key)
    {
        if (!body.TryGetValue(key, out var value) || value is null) return 0;
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number)) return number;
            return 0;
        }
        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) ? parsed : 0;
    }

    private static List<(int Id, string Name)> BodyChapters(Dictionary<string, object?> body)
    {
        if (!body.TryGetValue("chapters", out var value) || value is null) return new List<(int, string)>();
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Array) return new List<(int, string)>();
        var result = new List<(int, string)>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = 0;
            var name = "";
            if (item.TryGetProperty("id", out var idElement))
            {
                if (idElement.ValueKind == JsonValueKind.Number) idElement.TryGetInt32(out id);
                else if (idElement.ValueKind == JsonValueKind.String) int.TryParse(idElement.GetString(), out id);
            }
            if (item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                name = NormalizeEditText(nameElement.GetString() ?? "");
            }
            if (id > 0) result.Add((id, name));
        }
        return result;
    }

    private static string TextForEdit(object? value, object? updateDate)
    {
        return NormalizeAssetPaths(DecryptField(value, updateDate) ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }

    private static string NormalizeEditText(string value)
    {
        return (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
    }

    /* ---- 选项解析：读 / 数 / 保存 / 还原 / 重贴的**唯一口径** ---------------------------
       ⚠️ 以前这里有**两套**口径，而且写侧用的是更粗的那套：
         · 读侧 ExtractOptions：行首标签之外还切行内标签；
         · 写侧 CountOptions（保存 / 还原 / 重贴共用）：只数**行首**标签、只按去重计数。
       于是「A. one B. two / C. three D. four」这种同行写法：读侧能解析出 4 个选项，
       写侧只写 ianswercount=2 → 学员端 Take(2) 把 C/D 截掉 → 正确答案的按钮根本不存在。
       反过来，题干里引用了一个标签（「A. see B. clause」后面才是真正的 B. 行）时，
       读侧把引用也当成选项（A/B/B/C/D），写侧写 4 → 截成 A/B/B/C，D 又被砍掉。
       现在只有一份解析（ParseOptions）：读取、计数、保存、还原、重贴全用它，
       计数**不再用来截断**真实选项 —— 它只是"这道题有几个选项"的元数据。
       （拉取层不能引用本工程，LocalEdits.cs 里有一份逐字等价的镜像，改这里必须同步那边，
         有 scripts/verify-puller-sync.ps1 兜底。） */

    /// <summary>一个解析出来的选项。</summary>
    private sealed record OptionItem(string Label, string Text);

    /// <summary>规范解析结果 + 给编辑器看的诊断（重复 / 非顺序标签）。</summary>
    private sealed record OptionParse(List<OptionItem> Options, List<string> Warnings);

    /// <summary>
    /// 题干（HTML 或纯文本）→ 规范选项列表。规则：
    ///   1. 行首标签是选项的骨架；同一标签重复出现时只有第一次算选项，后面那几行并进上一个选项的
    ///      正文（实测 22421001 的选项块后面又跟了一遍 C./D. 行、24298363 跟着 B./D. 开头的解释行）。
    ///   2. 行内标签只有「不是任何行首标签、且还没出现过」时才当成选项分隔点 ——
    ///      题干里引用标签（「A. see B. clause」后面才是真正的 B. 行）不会被切碎。
    ///   3. 文本一律先过 StripHtml（<br> → 换行、去标签、HtmlDecode），\r 归一成 \n。
    /// </summary>
    private static OptionParse ParseOptions(string? html)
    {
        var text = StripHtml(html ?? "").Replace("\r", "\n");
        var warnings = new List<string>();

        var lineLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var heads = new List<Match>();
        foreach (Match match in OptionLabelRegex.Matches(text))
        {
            var label = match.Groups[1].Value.ToUpperInvariant();
            if (!lineLabels.Add(label))
            {
                warnings.Add($"选项标签 {label} 重复出现：只有第一次算选项，后面那几行并入上一个选项的正文");
                continue;
            }
            heads.Add(match);
        }

        var options = new List<OptionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < heads.Count; i++)
        {
            var start = heads[i].Index + heads[i].Length;
            var end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
            SplitOptionSegment(heads[i].Groups[1].Value, text.Substring(start, Math.Max(0, end - start)),
                options, seen, lineLabels);
        }

        var labels = options.Select(option => option.Label.ToUpperInvariant()).ToList();
        if (labels.Count > 0)
        {
            var missing = new List<string>();
            for (var i = 0; i < labels.Count; i++)
            {
                var expected = ((char)('A' + i)).ToString();
                if (!string.Equals(labels[i], expected, StringComparison.Ordinal)) missing.Add(expected);
            }
            if (missing.Count > 0)
            {
                warnings.Add($"选项标签不是 A 起连续编号：解析出 {string.Join("、", labels)}" +
                             $"（期望 A、B、C…；缺 {string.Join("、", missing)}）");
            }
        }
        return new OptionParse(options, warnings);
    }

    /// <summary>
    /// 题干里能数出几个选项 —— 直接数 <see cref="ParseOptions"/> 的规范结果
    /// （不再是"行首标签去重个数"：同行写法的选项会被那个口径漏掉）。
    /// 2..8 之外一律记 0，保持旧语义：0 = 不作约束，学员端不截断。
    /// </summary>
    private static int CountOptions(string? text)
    {
        var count = ParseOptions(text).Options.Count;
        return count is >= 2 and <= 8 ? count : 0;
    }

    /// <summary>
    /// 编辑器用的选项体检：解析口径与读侧完全一致（题干优先，题干里解析不出选项再看补充题干），
    /// 把「重复 / 非顺序标签、答案不在选项里、库里计数与解析结果不一致」直接摆给编辑看。
    /// </summary>
    private static List<string> AuditOptions(string? title, string? question, string? answer, int storedCount)
    {
        var parsed = ParseOptions(title);
        var source = "题干";
        if (parsed.Options.Count == 0)
        {
            var fromQuestion = ParseOptions(question);
            if (fromQuestion.Options.Count > 0)
            {
                parsed = fromQuestion;
                source = "补充题干";
            }
        }

        var warnings = new List<string>(parsed.Warnings);
        var labels = parsed.Options.Select(option => option.Label.ToUpperInvariant()).ToList();
        var answerLabels = Regex.Matches(answer ?? "", "[A-H]")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (labels.Count > 0)
        {
            var absent = answerLabels.Where(label => !labels.Contains(label, StringComparer.Ordinal)).ToList();
            if (absent.Count > 0)
            {
                warnings.Add($"正确答案 {string.Join("、", absent)} 不在{source}解析出的选项" +
                             $"（{string.Join("、", labels)}）里：学员端选不中正确答案");
            }
        }
        if (storedCount > 0 && labels.Count > 0 && storedCount != labels.Count)
        {
            warnings.Add($"库里的 ianswercount={storedCount} 与{source}解析出的 {labels.Count} 个选项不一致" +
                         "（保存后按解析结果回写）");
        }
        return warnings;
    }

    private List<Dictionary<string, object?>> Query(string sql, Dictionary<string, object?> parameters)
    {
        if (!File.Exists(_paths.SqlitePath)) throw new FileNotFoundException("SQLite question bank not found. Upload or mount data/question-bank.db first.", _paths.SqlitePath);
        // 读路径统一走 OpenBank：主库 + 拉取库（未购课程）合起来看，不带库名的表名会命中遮蔽视图。
        using var conn = OpenBank(_paths.SqlitePath, _paths.PulledSqlitePath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var pair in parameters) cmd.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var result = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    private static string DecryptField(object? value, object? updateDate)
    {
        var text = ToStr(value);
        if (text.Length == 0) return "";
        var decrypted = Decrypt(text, GetTicks(updateDate));
        return decrypted.Length > 0 ? decrypted : text;
    }

    private static string GetTicks(object? value)
    {
        if (value is null || value is DBNull) return "";
        return Convert.ToDateTime(value, CultureInfo.InvariantCulture).Ticks.ToString(CultureInfo.InvariantCulture);
    }

    private static string Decrypt(string encrypted, string suffix)
    {
        try
        {
            var key = suffix.Length == 0 ? "china" : suffix;
            var encryptedBytes = Convert.FromBase64String(encrypted);
            var legacyEncoding = Encoding.GetEncoding("GB18030");
            var keyBytes = legacyEncoding.GetBytes(key);
            using var md5 = MD5.Create();
            using var triple = TripleDES.Create();
            triple.Key = md5.ComputeHash(keyBytes);
            triple.Mode = CipherMode.ECB;
            triple.Padding = PaddingMode.PKCS7;
            using var transform = triple.CreateDecryptor();
            var plain = transform.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
            return legacyEncoding.GetString(plain);
        }
        catch
        {
            return "";
        }
    }

    /* 选项标签出现在「行首」的形式（老口径，不变）。 */
    private static readonly Regex OptionLabelRegex = new(@"(?m)(?:^|\n)\s*([A-H])[\.\u3001\uff0e:：]\s*");

    /* 行内标签：同一行里写了两个及以上选项（实测 1509065 的「A.11. 34 B. 11. 33」、
       1509104 的「A.甲 B.乙 C、丙 D. 丁」、1512051 的「…B. F1-F3-F2 C, …  D. F3-F1-F2」）。
       分隔符后**不加**空白要求 —— 实测 1509100 的「132 D、66; 99」在 D 后面直接跟内容，
       加了 \s 就切不开，而它的正确答案正是 D（选项里没有 D，学员无法作答）。
       标签前的边界：空白（会被当分隔符吃掉）**或**一个汉字（零宽，必须保留 ——
       实测 1509074 的「合理的施工组织设计C.生产要素消耗水平」中间没有空格，
       老口径整个 C 选项被吞进 B 的正文，全库 1600+ 道题受影响）。 */
    private static readonly Regex InlineOptionLabelRegex = new(@"(?:\s|(?<=[\u4e00-\u9fff]))([A-H])[\.\u3001\uff0e:：]\s*");

    /// <summary>
    /// 学员端读侧：题干 → 选项。与 <see cref="CountOptions"/> 共用 <see cref="ParseOptions"/>，
    /// 所以"写进去的计数"与"读出来的选项数"不可能再对不上。
    /// <paramref name="answerCount"/> 保留在签名里（调用点与探针都按它反射），但**不再用来截断**：
    /// 库里那个数可能是旧保存路径写错的小值（或上游就没跟上题干），拿它 Take() 会砍掉真实选项。
    /// 实测整库扫描：解析数 > 库里计数 的行只有 2 条，而那 2 条恰恰是库里计数错的。
    /// </summary>
    private static List<object> ExtractOptions(string html, int answerCount)
    {
        _ = answerCount;
        return ParseOptions(html).Options
            .Select(option => (object)new { label = option.Label, text = option.Text })
            .ToList();
    }

    /// <summary>
    /// 把「行首标签 + 整段正文」再按**行内标签**切开：同一行写了两个选项时，后一个会被
    /// 前一个吞进正文（实测 1509065 的 A 里混进「11. 34 B.」、1509100 的正确答案 D
    /// 根本不在选项里、1509104 只剩 1 个选项 → 学员无法作答）。
    /// 只切「既不是行首标签、也没出现过」的标签，避免把正文里引用的标签当成分隔点。
    /// </summary>
    private static void SplitOptionSegment(string label, string body, List<OptionItem> parsed,
        HashSet<string> seen, HashSet<string> lineLabels)
    {
        var pendingLabel = label;
        var rest = body;
        while (true)
        {
            var cut = FindInlineOptionLabel(rest, seen, pendingLabel, lineLabels);
            var head = (cut is null ? rest : rest[..cut.Index]).Trim();
            if (head.Length > 0) parsed.Add(new OptionItem(pendingLabel, head));
            seen.Add(pendingLabel);
            if (cut is null) return;
            pendingLabel = cut.Groups[1].Value;
            rest = rest[(cut.Index + cut.Length)..];
        }
    }

    private static Match? FindInlineOptionLabel(string body, HashSet<string> seen, string pendingLabel, HashSet<string> lineLabels)
    {
        for (var match = InlineOptionLabelRegex.Match(body); match.Success; match = match.NextMatch())
        {
            var label = match.Groups[1].Value.ToUpperInvariant();
            if (lineLabels.Contains(label)) continue;   // 是行首标签 → 正文里的标签引用，不是分隔点
            if (seen.Contains(label)) continue;         // 已经出现过（含正在拼的这一段）
            if (label.Equals(pendingLabel, StringComparison.OrdinalIgnoreCase)) continue;
            return match;
        }
        return null;
    }

    private static string RemoveOptions(string html)
    {
        /* 题干里的选项列表是按行排的（NormalizeHtml 已经把 \n 转成 <br>），
           所以按 <br> 切行，找到第一行「以 A. / B、 开头」的，把它和后面的都丢掉。

           这里**不能**再走 StripHtml + HtmlEncode 的老路子 —— 那会把题干里的
           <img> 连同 HTML 一起丢掉。线上有 2237 道题的题图挂在题干上，凡是题干
           里带选项列表的，题图全被那一步吃掉；像
             <img src=".../1509093_0.jpg">\n \nA. 109.9 B. 110.3\n...
           这种「题干就是一张图」的题，整段 stem 直接变成空串。 */
        var parts = Regex.Split(html, @"(?i)(<br\s*/?>)");
        var cut = -1;
        for (var i = 0; i < parts.Length; i += 2)
        {
            if (IsOptionLineStart(parts[i])) { cut = i; break; }
        }
        if (cut < 0) return html;
        if (cut == 0) return "";                 // 整段就是选项，没有题干
        var kept = string.Concat(parts.Take(cut)).Trim();
        return kept.Length > 0 ? kept : html;
    }

    private static bool IsOptionLineStart(string line)
    {
        var text = StripHtml(line).TrimStart();
        return Regex.IsMatch(text, @"^[A-H][\.\u3001\uff0e:：]\s*");
    }

    private static string NormalizeHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = NormalizeAssetPaths(html);
        return html.Replace("\r\n", "\n").Replace("\n", "<br>");
    }

    private static string NormalizeAssetPaths(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = html.Replace("\\", "/", StringComparison.Ordinal);
        html = Regex.Replace(html, @"(?i)(src=[""']?)(?:file:///)?[a-z]:/[^""'>\s]*?/jinkaodian/data/", "$1/assets/");
        html = Regex.Replace(html, @"(?i)(src=[""']?)(?:file:///android_asset/)?(?:\.\./)+data/", "$1/assets/");
        html = Regex.Replace(html, @"(?i)(src=[""']?)(?:file:///android_asset/)?data/", "$1/assets/");
        return html;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<.*?>", "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static int ToInt(object? value) => value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static string ToStr(object? value) => value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    private static string ToDateString(object? value) => value is null or DBNull ? "" : Convert.ToDateTime(value, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// 只读查询入口。给 <see cref="ChapterMapStore"/> 用——章节映射重建要扫全库。
    /// </summary>
    public List<Dictionary<string, object?>> ReadAll(string sql, Dictionary<string, object?>? parameters = null)
        => Query(sql, parameters ?? new Dictionary<string, object?>());

    /// <summary>
    /// 题干纯文本：解密 → 去标签 → 压空白。
    /// 章节映射按它建键，建表与查表必须走同一个函数，否则永远匹配不上。
    /// </summary>
    public static string PlainStem(object? title, object? updateDate)
        => Regex.Replace(StripHtml(DecryptField(title, updateDate)), @"\s+", " ").Trim();

    /// <summary>解析的纯文本。口径同 <see cref="PlainStem"/>，但读的是 cdescription。</summary>
    public static string PlainDescription(object? description, object? updateDate)
        => Regex.Replace(StripHtml(DecryptField(description, updateDate)), @"\s+", " ").Trim();

    /// <summary>
    /// 章节映射的键：题干去掉所有非中文/字母/数字的字符后取前 120 个。
    /// 去标点空白是为了让「同一道题在不同卷册里的排版差异」不影响匹配。
    /// </summary>
    public static string StemKey(string plainStem) => TextKey(plainStem, 120);

    /// <summary>解析的键。解析比题干长，取前 300 个字符。</summary>
    public static string DescriptionKey(string plainDescription) => TextKey(plainDescription, 300);

    /// <summary>把一段纯文本压成匹配键：只保留中文/字母/数字，截断到 maxChars。</summary>
    public static string TextKey(string plain, int maxChars)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var sb = new StringBuilder(maxChars);
        foreach (var ch in plain)
        {
            if (sb.Length >= maxChars) break;
            if ((ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '\u4e00' && ch <= '\u9fff'))
            {
                sb.Append(ch >= 'A' && ch <= 'Z' ? (char)(ch + 32) : ch);
            }
        }
        return sb.ToString();
    }

    private static string MaxDateString(params object?[] values)
    {
        DateTime? max = null;
        foreach (var value in values)
        {
            if (value is null or DBNull) continue;
            var date = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
            if (!max.HasValue || date > max.Value) max = date;
        }
        return max.HasValue ? max.Value.ToString("yyyy-MM-dd HH:mm:ss") : "";
    }
}

/// <summary>外挂映射文件的内容。只含章名表和 subjectId → 章索引，不含任何题干。</summary>
sealed class ChapterMapFile
{
    public int Version { get; set; } = 1;
    public string GeneratedAt { get; set; } = "";
    /// <summary>生成时的题库签名。与当前签名不一致说明题库换过，映射过期。</summary>
    public string BankSignature { get; set; } = "";
    public List<string> Chapters { get; set; } = new();
    /// <summary>subjectId → 课本章在 Chapters 里的下标。一题多章时数组长度 &gt; 1。</summary>
    public Dictionary<string, int[]> Map { get; set; } = new(StringComparer.Ordinal);
    public int MappedQuestions { get; set; }
    public int MultiChapterQuestions { get; set; }
}

/// <summary>
/// 「整卷题 → 课本章」的外挂映射。
///
/// 背景：题库的 coursechapter 是四层（卷册 grade0 → 章 grade1 → 节 grade2 → 目 grade3）。
/// 章节练习 / 章节密训 / 章节精选 这类卷册把题挂在「节」上，天然带课本章归属；
/// 而 历年真题 / 模考 / 预测 / 高频考点 这类是**整卷**，题直接挂在「卷」上，
/// 题库里根本没有章节这个维度（14259 题，占全库 48%）。
///
/// 好在整卷题里有相当一部分在章节型卷册里有一份**完全相同的题干**，那份已经归好章了 ——
/// 按题干查表就能把章节抄过来（实测 6967 题，其中 564 题一题多章）。
/// 刻意不做相似度猜测：那部分留一验证只有 81~88% 的精度，错归会直接污染薄弱章节统计。
///
/// 为什么不写进 question-bank.db：整包上传会替换那个文件，写进去就没了。
/// 所以落成 data/chapter-map.json 这个独立文件，题库更新后由管理端重建。
/// </summary>
sealed class ChapterMapStore
{
    private readonly AppPaths _paths;
    private readonly QuestionBank _bank;
    private readonly IConfiguration? _configuration;
    private readonly object _gate = new();
    private ChapterMapFile? _cache;
    private string _cacheKey = "";

    public ChapterMapStore(AppPaths paths, QuestionBank bank, IConfiguration? configuration = null)
    {
        _paths = paths;
        _bank = bank;
        _configuration = configuration;
    }

    /// <summary>
    /// 重建的规模上限（题数）。超过就直接放弃，不冒险。
    ///
    /// 为什么需要：重建会把「主库 + 拉取库」的全部题目读进内存建索引。
    /// 本机开发库几万题没问题，但拉取库可能是整个上游目录 ——
    /// 实测某套部署的 <c>jkd-pulled.db</c> 有 4.68GB / 521 万题 / 659 门课，
    /// 那种规模下这个算法会直接吃光内存。宁可没有映射，也不能把进程搞崩。
    /// 可用 <c>App:ChapterMapMaxQuestions</c> 调整。
    /// </summary>
    private int MaxQuestions
    {
        get
        {
            var configured = _configuration?.GetValue<int?>("App:ChapterMapMaxQuestions");
            return configured is > 0 ? configured.Value : 500_000;
        }
    }

    public string FilePath
    {
        get
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_paths.SqlitePath)) ?? _paths.BaseRoot;
            return Path.Combine(dir, "chapter-map.json");
        }
    }

    /// <summary>题库文件签名。整包上传或重新拉取后签名会变，映射随之过期。</summary>
    public string BankSignature()
    {
        static string Of(string path)
        {
            if (!File.Exists(path)) return "-";
            var info = new FileInfo(path);
            return info.Length.ToString(CultureInfo.InvariantCulture)
                   + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        return Of(_paths.SqlitePath) + "|" + Of(_paths.PulledSqlitePath);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>读映射（带缓存）。文件缺失或读坏一律返回空映射，不抛。</summary>
    public ChapterMapFile Load()
    {
        var key = CacheKey();
        lock (_gate)
        {
            if (_cache is not null && _cacheKey == key) return _cache;
            _cache = TryRead() ?? new ChapterMapFile();
            _cacheKey = key;
            return _cache;
        }
    }

    private string CacheKey()
    {
        var path = FilePath;
        var mapStamp = File.Exists(path)
            ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)
            : "-";
        return BankSignature() + "|" + mapStamp;
    }

    private ChapterMapFile? TryRead()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;
            var parsed = JsonSerializer.Deserialize<ChapterMapFile>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            if (parsed is null) return null;
            parsed.Chapters ??= new List<string>();
            parsed.Map ??= new Dictionary<string, int[]>(StringComparer.Ordinal);
            parsed.BankSignature ??= "";
            parsed.GeneratedAt ??= "";
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    public object Status()
    {
        var map = Load();
        var stale = map.BankSignature.Length > 0 && map.BankSignature != BankSignature();
        return new
        {
            ok = true,
            present = map.Map.Count > 0,
            stale,
            generatedAt = map.GeneratedAt,
            chapters = map.Chapters.Count,
            mappedQuestions = map.MappedQuestions,
            multiChapterQuestions = map.MultiChapterQuestions,
            file = FilePath,
        };
    }

    /// <summary>
    /// 页码信号的采信门槛：同一页码下，最高票那个章至少要占全部票数的这个比例。
    /// 低于门槛说明这一页跨了好几个章（页码在每节重新开始），猜了基本是错的。
    /// </summary>
    private const double PageVoteConfidence = 0.9;

    /// <summary>
    /// 解析里的教材页码引用，如「2025教材P346」「参见教材P176」。
    /// 整卷题里 27.6% 的解析带这个，而页码和章节几乎一一对应 ——
    /// 用它反推章节，留一验证 99.3% 命中。
    /// </summary>
    private static readonly Regex PageRefRegex = new(@"(?:(\d{4})\s*教材|教材)\s*[Pp]\s*(\d{1,4})", RegexOptions.Compiled);

    /// <summary>没有「教材」字样时的兜底：「参见 P176」。</summary>
    private static readonly Regex PageAnyRegex = new(@"[Pp]\s*(\d{1,4})", RegexOptions.Compiled);

    /// <summary>取解析里的页码引用。年份为空表示没写明年份（跨版本时用页码兜底）。</summary>
    private static List<(string Year, int Page)> PageRefs(string description)
    {
        var result = new List<(string, int)>();
        if (description.Length == 0) return result;
        foreach (Match match in PageRefRegex.Matches(description))
        {
            if (int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page > 0)
            {
                result.Add((match.Groups[1].Value, page));
            }
        }
        if (result.Count > 0) return result;
        foreach (Match match in PageAnyRegex.Matches(description))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page > 0)
            {
                result.Add(("", page));
            }
        }
        return result;
    }

    /// <summary>
    /// 章名的规范形式：把任意空白（含全角空格 U+3000）压成一个半角空格再 trim。
    ///
    /// 题库里同一个课本的章在不同卷册里会用不同的空格写法 —— 实测课程 570 同时存在
    /// 「第二章　安装工程计量」(U+3000) 和「第二章 安装工程计量」(半角)，
    /// 全库去掉空白后重名的章有 102 组、涉及 414 个 chapter 节点。
    /// 不归一化就会被当成两个不同的章，一题多章的统计直接虚高。
    ///
    /// 前端的 textbookChapterName 必须做同样的归一化，否则两边对不上键。
    /// </summary>
    public static string NormalizeChapterName(string? name)
        => Regex.Replace(name ?? "", @"\s+", " ").Trim();

    private sealed class ChapterNode
    {
        public int Id { get; init; }
        public int CourseId { get; init; }
        public int Type { get; init; }
        public int Grade { get; init; }
        public string Name { get; init; } = "";
        public string Code { get; init; } = "";
        public int ParentId { get; set; }
    }

    /// <summary>扫全库重建映射并落盘。</summary>
    public object Rebuild()
    {
        if (!File.Exists(_paths.SqlitePath))
        {
            throw new FileNotFoundException("题库不存在，先上传或挂载 data/question-bank.db", _paths.SqlitePath);
        }

        // 先数一遍题量再决定要不要往下走：读全库建索引是内存大户，超限直接放弃。
        var totalQuestions = Convert.ToInt32(
            _bank.ReadAll("select count(*) as n from coursesubject where coalesce(bstopflag,0)=0")
                .FirstOrDefault()?.GetValueOrDefault("n") ?? 0,
            CultureInfo.InvariantCulture);
        if (totalQuestions > MaxQuestions)
        {
            throw new InvalidOperationException(
                $"题库共 {totalQuestions} 题，超过章节映射的重建上限 {MaxQuestions} 题，已跳过。"
                + "映射需要把全部题目读进内存建索引，这个规模会吃光内存。"
                + "如果确实要建，调大 App:ChapterMapMaxQuestions。");
        }

        var chapterRows = _bank.ReadAll(
            "select ichapterid, icourseid, cchaptername, cchaptercode, igrade, itype "
            + "from coursechapter where coalesce(bstopflag,0)=0 order by itype, cchaptercode");
        var questionRows = _bank.ReadAll(
            "select isubjectid, icourseid, ichapterid, ctitle, cdescription, dupdatedate "
            + "from coursesubject where coalesce(bstopflag,0)=0");

        var nodes = new List<ChapterNode>(chapterRows.Count);
        foreach (var row in chapterRows)
        {
            nodes.Add(new ChapterNode
            {
                Id = AsInt(row, "ichapterid"),
                CourseId = AsInt(row, "icourseid"),
                Type = AsInt(row, "itype"),
                Grade = AsInt(row, "igrade"),
                Name = AsText(row, "cchaptername"),
                Code = AsText(row, "cchaptercode"),
            });
        }
        var byId = new Dictionary<int, ChapterNode>(nodes.Count);
        foreach (var node in nodes) byId[node.Id] = node;

        // 父节点判定与前端 buildChapterTree 的**意图**一致：
        // 同课程 + 同 itype + grade 差 1 + 父 code 是子 code 的前缀且更短，
        // 并在多个候选里取 **code 最长**的那个（前端是 .sort(b.code.length - a.code.length)[0]）。
        // 取第一个匹配是不对的：题库里同一层级可能有两个前缀都成立，选错就会挂到别的章下。
        //
        // 但两边**并非逐字一致**：前端 app.js 是 node.code.startsWith(item.code)（大小写敏感），
        // 这里（以及 GetChapterSubtree 里那处）用的是 StartsWith(..., OrdinalIgnoreCase)（不敏感）。
        // 当前题库 773/773 条 cchaptercode 全是纯数字，两种语义逐行算父节点差异 0 行，因此实际等价。
        // 若将来出现含字母的 code，前后端行为就会分叉：例如 "0139A02".startsWith("0139a") 前端为
        // false、后端为 true。届时再统一语义，而不是现在改比较逻辑——改了没收益，还可能引入回归。
        foreach (var node in nodes)
        {
            if (node.Code.Length == 0) continue;
            ChapterNode? best = null;
            foreach (var candidate in nodes)
            {
                if (candidate.Id == node.Id) continue;
                if (candidate.CourseId != node.CourseId || candidate.Type != node.Type) continue;
                if (candidate.Grade != node.Grade - 1) continue;
                if (candidate.Code.Length == 0 || candidate.Code.Length >= node.Code.Length) continue;
                if (!node.Code.StartsWith(candidate.Code, StringComparison.OrdinalIgnoreCase)) continue;
                if (best is null || candidate.Code.Length > best.Code.Length) best = candidate;
            }
            if (best is not null) node.ParentId = best.Id;
        }

        var pathCache = new Dictionary<int, List<ChapterNode>>();
        List<ChapterNode> PathOf(int chapterId)
        {
            if (pathCache.TryGetValue(chapterId, out var cached)) return cached;
            var chain = new List<ChapterNode>();
            var current = byId.TryGetValue(chapterId, out var node) ? node : null;
            var guard = 0;
            while (current is not null && guard++ < 16)
            {
                chain.Insert(0, current);
                current = current.ParentId != 0 && byId.TryGetValue(current.ParentId, out var parent) ? parent : null;
            }
            pathCache[chapterId] = chain;
            return chain;
        }

        // 第一遍：给每道题算出章节路径，同时记下哪些卷册带「节」（depth >= 3）。
        var parsed = new List<(int Id, int CourseId, int VolumeId, string Stem, string Desc, List<ChapterNode> Path)>(questionRows.Count);
        var volumesWithSections = new HashSet<(int, int)>();
        foreach (var row in questionRows)
        {
            var path = PathOf(AsInt(row, "ichapterid"));
            if (path.Count < 2) continue;
            var courseId = AsInt(row, "icourseid");
            if (path.Count >= 3) volumesWithSections.Add((courseId, path[0].Id));
            parsed.Add((AsInt(row, "isubjectid"), courseId, path[0].Id,
                QuestionBank.StemKey(QuestionBank.PlainStem(row.GetValueOrDefault("ctitle"), row.GetValueOrDefault("dupdatedate"))),
                QuestionBank.DescriptionKey(QuestionBank.PlainDescription(row.GetValueOrDefault("cdescription"), row.GetValueOrDefault("dupdatedate"))),
                path));
        }

        // 第二遍：章节型卷册（有节）当语料，建三个索引，按可靠性从高到低用：
        //   ① 题干指纹   —— 同一道题的排版差异最小
        //   ② 解析指纹   —— 题干有细微差异时（题号、空格、选项换行）还能对上
        //   ③ 教材页码   —— 解析里「2025教材P346」这种引用，反推出的章节留一验证 99.4% 命中
        // 索引都必须**按课程**分：不同课程可能有完全相同的题干/页码却归在不同章下。
        // 同一题干在同一课程内命中多个章 = 一题多章。
        var stemIndex = new Dictionary<(int CourseId, string Key), HashSet<string>>();
        var descIndex = new Dictionary<(int CourseId, string Key), HashSet<string>>();
        var pageIndex = new Dictionary<(int CourseId, string Year, int Page), Dictionary<string, int>>();
        var corpusRows = 0;
        var corpusEmptyStem = 0;
        foreach (var row in parsed)
        {
            // 语料来自**章节型**卷册（有节的那些），所以这里要 !Contains —— 反过来就一行都取不到。
            if (!volumesWithSections.Contains((row.CourseId, row.VolumeId))) continue;
            if (row.Path.Count < 3) continue;   // 只取挂在「节」上的题
            corpusRows++;
            if (row.Stem.Length == 0)
            {
                corpusEmptyStem++;
                continue;
            }
            var chapter = NormalizeChapterName(row.Path[1].Name);

            Add(stemIndex, (row.CourseId, row.Stem), chapter);
            if (row.Desc.Length > 0) Add(descIndex, (row.CourseId, row.Desc), chapter);
            foreach (var (year, page) in PageRefs(row.Desc))
            {
                var key = (row.CourseId, year, page);
                if (!pageIndex.TryGetValue(key, out var votes)) pageIndex[key] = votes = new Dictionary<string, int>(StringComparer.Ordinal);
                votes[chapter] = votes.GetValueOrDefault(chapter) + 1;
            }
        }

        static void Add(Dictionary<(int, string), HashSet<string>> index, (int, string) key, string chapter)
        {
            if (!index.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                index[key] = set;
            }
            set.Add(chapter);
        }

        // 第三遍：整卷型卷册（无节）的题，查表抄章节。
        var chapterNames = new List<string>();
        var chapterIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int IndexOf(string name)
        {
            if (chapterIndex.TryGetValue(name, out var i)) return i;
            chapterNames.Add(name);
            chapterIndex[name] = chapterNames.Count - 1;
            return chapterNames.Count - 1;
        }

        var map = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var multi = 0;
        var volumeRows = 0;
        var viaStem = 0;
        var viaDesc = 0;
        var viaPage = 0;
        foreach (var row in parsed)
        {
            if (volumesWithSections.Contains((row.CourseId, row.VolumeId))) continue;
            volumeRows++;

            HashSet<string>? chapters = null;
            var source = "";
            if (row.Stem.Length > 0 && stemIndex.TryGetValue((row.CourseId, row.Stem), out var byStemHit) && byStemHit.Count > 0)
            {
                chapters = byStemHit;
                source = "stem";
            }
            if (chapters is null && row.Desc.Length > 0 && descIndex.TryGetValue((row.CourseId, row.Desc), out var byDescHit) && byDescHit.Count > 0)
            {
                chapters = byDescHit;
                source = "desc";
            }
            if (chapters is null && row.Desc.Length > 0)
            {
                // 页码：同一页可能对应多个章（页码在每节重新开始，低页码尤其混）。
                // 只在投票够集中时才采信 —— 实测线上题库 1566 个 (课程,年份,页码) 键里
                // 99.0% 的最高票占比 ≥0.9，剩下那 0.6% 猜出来的基本是错的
                // （例：某题解析写「见教材第一章第一节P1」，P1 在语料里散在三个章，
                // 多数票投给了第五章）。宁可留空，也不写错。
                var best = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (year, page) in PageRefs(row.Desc))
                {
                    if (!pageIndex.TryGetValue((row.CourseId, year, page), out var votes) || votes.Count == 0) continue;
                    var total = votes.Values.Sum();
                    var top = votes.OrderByDescending(pair => pair.Value).First();
                    if (total <= 0 || top.Value / (double)total < PageVoteConfidence) continue;
                    best[top.Key] = Math.Max(best.GetValueOrDefault(top.Key), top.Value);
                }
                if (best.Count > 0)
                {
                    chapters = new HashSet<string>(best.Keys, StringComparer.Ordinal);
                    source = "page";
                }
            }
            if (chapters is null || chapters.Count == 0) continue;

            var indices = chapters.Select(IndexOf).OrderBy(x => x).ToArray();
            map[row.Id.ToString(CultureInfo.InvariantCulture)] = indices;
            if (indices.Length > 1) multi++;
            if (source == "stem") viaStem++;
            else if (source == "desc") viaDesc++;
            else viaPage++;
        }

        var payload = new ChapterMapFile
        {
            Version = 1,
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            BankSignature = BankSignature(),
            Chapters = chapterNames,
            Map = map,
            MappedQuestions = map.Count,
            MultiChapterQuestions = multi,
        };

        var filePath = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? _paths.BaseRoot);
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, filePath, true);

        lock (_gate)
        {
            _cache = payload;
            _cacheKey = CacheKey();
        }

        var volumeQuestions = parsed.Count(r => !volumesWithSections.Contains((r.CourseId, r.VolumeId)));
        return new
        {
            ok = true,
            chapters = chapterNames.Count,
            sectionQuestions = parsed.Count - volumeQuestions,
            volumeQuestions,
            mappedQuestions = map.Count,
            multiChapterQuestions = multi,
            // 诊断用：三条信号各命中多少、语料两侧的规模。
            // 匹配为 0 时这些数字能立刻区分「索引没建起来」和「题库里确实没有可用的重复题」。
            corpusRows,
            corpusEmptyStem,
            corpusStems = stemIndex.Count,
            corpusDescs = descIndex.Count,
            corpusPages = pageIndex.Count,
            volumeRows,
            viaStem,
            viaDesc,
            viaPage,
            sampleStem = parsed.FirstOrDefault(r => r.Stem.Length > 0).Stem,
            file = filePath,
            message = $"已重建章节映射：{volumeQuestions} 道整卷题中归入 {map.Count} 道"
                      + (multi > 0 ? $"（其中 {multi} 道一题多章）" : ""),
        };
    }

    /// <summary>
    /// 题库换过（拉取更新 / 整包上传）之后重建映射。
    /// 失败只记日志、不往上抛 —— 拉取本身已经成功了，不能因为映射重建失败就让整个操作报错。
    /// </summary>
    public bool TryRebuildQuietly(string reason)
    {
        try
        {
            Rebuild();
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [chapter-map] {reason}后已重建章节映射");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [chapter-map] {reason}后重建失败（不影响题库本身）: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 启动自检：映射文件缺失或已过期就在**后台**补一次，免得首次部署还要手动点重建。
    /// 只做一次、失败不重试，也不阻塞启动。
    /// </summary>
    public void WarmUpInBackground()
    {
        if (!File.Exists(_paths.SqlitePath)) return;
        var map = Load();
        if (map.Map.Count > 0 && map.BankSignature == BankSignature()) return;
        Task.Run(() => TryRebuildQuietly(map.Map.Count == 0 ? "启动自检（映射缺失）" : "启动自检（映射过期）"));
    }

    private static int AsInt(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) && value is not null and not DBNull
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;

    private static string AsText(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) && value is not null and not DBNull
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            : "";
}

/* ══════════════════════════════════════════════════════════════════════════
   每日推送提醒
   ──────────────────────────────────────────────────────────────────────────
   用户绑一个渠道（Bark / ntfy / 通用 webhook），PushScheduler 每 30 秒 tick 一次，
   到点就发一条「该练了」。为什么只能做在服务端：页面一关就没有 JS，而 Web Push 需要
   HTTPS 公网 + VAPID 密钥，本项目是 127.0.0.1 反代、SW 只做静态缓存，不划算。

   数据放哪：userdata/push/<user>.json（**子目录**）。三条理由：
     1. 用户数据整包 userdata/<user>.json 由**客户端独占整包覆盖写**，服务端要动它
        必须走 revision/If-Match，每次推送前写一次就是主动制造 409 冲突；
     2. lastSentDate / failCount 是**服务端状态**，天然不属于客户端数据；
     3. webhook token 不该进「导出我的数据」的包。
   为什么必须是子目录：/api/admin/users（Program.cs:356）和 /api/admin/data/status
   （:2486）都按**顶层** *.json 枚举账号，配置文件放顶层会凭空多出一个账号。
   ══════════════════════════════════════════════════════════════════════════ */

sealed class PushConfig
{
    public bool Enabled { get; set; }
    public string Time { get; set; } = "20:00";
    public int GraceHours { get; set; } = 6;
    public string Style { get; set; } = "milestone";   // remind | milestone | digest
    public string BaseUrl { get; set; } = "";
    public PushChannelConfig Channel { get; set; } = new();

    /* 以下全是**服务端状态**：PushConfigStore.Save 只白名单合并用户可编辑字段，
       客户端改不动它们。否则前端一次误传就能清掉 lastSentDate，同一天重复推送。 */
    public string LastSentDate { get; set; } = "";
    public string LastSentAt { get; set; } = "";
    public string LastResult { get; set; } = "";
    public string LastError { get; set; } = "";
    public int FailCount { get; set; }
    public string NextRetryAt { get; set; } = "";
    /// <summary>上次已经发过的连续天数里程碑。断签（streak 变小）时归零重来。</summary>
    public int MilestoneStreak { get; set; }
}

sealed class PushChannelConfig
{
    /// <summary>bark | ntfy | webhook | wechat | serverchan | pushplus | webpush</summary>
    public string Type { get; set; } = "webpush";
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>仅 webhook 用：正文模板，占位符 {{title}} {{body}} {{url}}（会做 JSON 转义）。</summary>
    public string Template { get; set; } = "";
    /// <summary>
    /// 仅 webpush 用：绑定的设备。一个账号可以绑多台（手机 + 电脑），
    /// 发送时逐台推，全部失败才算失败。
    /// 这一项**客户端改不动**（Save 会保留原值），只能通过 /api/push/subscribe 增删。
    /// </summary>
    public List<PushSubscription> Subscriptions { get; set; } = new();
}

/// <summary>浏览器推送订阅（PushSubscription.toJSON() 的形状）。</summary>
sealed class PushSubscription
{
    public string Endpoint { get; set; } = "";
    /// <summary>P-256 公钥（base64url，未压缩点，65 字节）。</summary>
    public string P256dh { get; set; } = "";
    /// <summary>16 字节认证密钥（base64url）。</summary>
    public string Auth { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string CreatedAt { get; set; } = "";

    public bool IsValid =>
        Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && P256dh.Length > 0 && Auth.Length > 0;
}

sealed class PushMessage
{
    public PushMessage(string title, string body, string url)
    {
        Title = title;
        Body = body;
        Url = url;
    }

    public string Title { get; }
    public string Body { get; }
    public string Url { get; }
}

/// <summary>服务端从用户数据里算出来的「今日摘要」。全部只读，不回写用户数据。</summary>
sealed class PushDigest
{
    public int Due { get; set; }
    public int WrongTotal { get; set; }
    public int DoneToday { get; set; }
    public int CorrectToday { get; set; }
    public int VerifiedToday { get; set; }
    public int Streak { get; set; }
    /// <summary>距最近一场考试的天数；-1 = 没有设置考试日期。</summary>
    public int DaysLeft { get; set; } = -1;
    public int DailyTarget { get; set; }
    public string CourseName { get; set; } = "";
    /// <summary>距上一次真正练过的天数；-1 = 从来没练过。</summary>
    public int DaysSinceLast { get; set; } = -1;
}

sealed class PushRunReport
{
    public int Sent { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Details { get; } = new();
}

/// <summary>推送配置的读写。线程安全：每个用户一把锁，写用「临时文件 + 原子替换」。</summary>
sealed class PushConfigStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly AppPaths _paths;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public PushConfigStore(AppPaths paths, IConfiguration configuration)
    {
        _paths = paths;
        /* 推送里「点一下跳回本站」的地址，部署相关，不进仓库：
           由 App:PublicBaseUrl 提供（docker-compose.override.yml 或 appsettings 里配）。
           没配就留空 —— 推送照发，只是不带链接，绝不去猜（反代后面拿不到真实 scheme，
           猜出来就是 http:// 开头，点开是错的）。 */
        _defaultBaseUrl = (configuration["App:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
    }

    private readonly string _defaultBaseUrl;

    /// <summary>新建配置时预填的跳转地址（也可以被用户改掉）。</summary>
    public string DefaultBaseUrl => _defaultBaseUrl;

    private PushConfig NewConfig() => new() { BaseUrl = _defaultBaseUrl };

    public string Root => Path.Combine(_paths.UserDataRoot, "push");
    public string PathFor(string user) => Path.Combine(Root, CleanUserName(user) + ".json");

    public PushConfig Read(string user)
    {
        var path = PathFor(user);
        try
        {
            if (!File.Exists(path)) return NewConfig();
            var body = File.ReadAllText(path, Encoding.UTF8);
            if (body.Trim().Length == 0) return NewConfig();
            var cfg = JsonSerializer.Deserialize<PushConfig>(body, JsonOptions) ?? NewConfig();
            /* 手写坏了的 JSON 里可能有 "channel": null —— 下面每处都直接点 Channel.Type，
               一个 null 就让整个调度器每轮抛异常。读的时候补回来最省事。 */
            cfg.Channel ??= new PushChannelConfig();
            cfg.Channel.Subscriptions ??= new List<PushSubscription>();
            cfg.Channel.Subscriptions.RemoveAll(s => s is null || !s.IsValid);
            /* 空 = 用服务端默认值。这样「老配置没这一项」和「用户清空了」都落到同一个
               结果上：点开通知能跳回站点，而不是一个没有链接的消息。 */
            if (cfg.BaseUrl.Trim().Length == 0) cfg.BaseUrl = _defaultBaseUrl;
            return cfg;
        }
        catch (Exception ex)
        {
            /* 配置坏了**不能**让调度器崩，也不能当成「已启用」乱发。回默认值（enabled=false）
               → 这一轮跳过、日志留痕；用户打开设置页保存一次就自动修好。 */
            Console.WriteLine($"[push] 读取推送配置失败（本轮按未启用处理）{path}: {ex.Message}");
            return NewConfig();
        }
    }

    /// <summary>给前端的形状：token 打码，只回后 4 位。</summary>
    public object ReadForApi(string user)
    {
        var cfg = Read(user);
        return new
        {
            ok = true,
            enabled = cfg.Enabled,
            time = cfg.Time,
            graceHours = cfg.GraceHours,
            style = cfg.Style,
            baseUrl = cfg.BaseUrl,
            channel = new
            {
                type = cfg.Channel.Type,
                url = cfg.Channel.Url,
                token = MaskToken(cfg.Channel.Token),
                tokenSet = cfg.Channel.Token.Length > 0,
                template = cfg.Channel.Template,
                /* 订阅端点本身就是一台设备的凭据（拿到就能冒充那台设备），
                   所以只回可读的摘要 + 一个稳定的短 id，不回完整 URL。 */
                subscriptions = cfg.Channel.Subscriptions
                    .Select(s => new
                    {
                        id = SubscriptionId(s.Endpoint),
                        host = HostOf(s.Endpoint),
                        userAgent = ShortUserAgent(s.UserAgent),
                        createdAt = s.CreatedAt,
                    })
                    .ToArray(),
                subscriptionCount = cfg.Channel.Subscriptions.Count,
            },
            lastSentDate = cfg.LastSentDate,
            lastSentAt = cfg.LastSentAt,
            lastResult = cfg.LastResult,
            lastError = cfg.LastError,
        };
    }

    public static PushConfig Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new PushConfig();
        return JsonSerializer.Deserialize<PushConfig>(body, JsonOptions) ?? new PushConfig();
    }

    /// <summary>保存用户可编辑的部分，保留服务端状态。</summary>
    public object Save(string user, PushConfig incoming)
    {
        var current = Read(user);
        var channel = incoming.Channel ?? new PushChannelConfig();

        current.Enabled = incoming.Enabled;
        current.Time = NormalizeTime(incoming.Time);
        current.GraceHours = incoming.GraceHours <= 0 ? 6 : Math.Clamp(incoming.GraceHours, 0, 23);
        current.Style = NormalizeStyle(incoming.Style);
        current.BaseUrl = NormalizeBaseUrl(incoming.BaseUrl);
        // 清空 = 回到服务端默认值（而不是「没有链接」）。
        if (current.BaseUrl.Length == 0) current.BaseUrl = _defaultBaseUrl;
        current.Channel = new PushChannelConfig
        {
            Type = NormalizeType(channel.Type),
            Url = (channel.Url ?? "").Trim(),
            Token = MergeToken(current.Channel.Token, channel.Token),
            Template = (channel.Template ?? "").Trim(),
            /* 订阅只能由 /api/push/subscribe 增删：前端保存设置时整个 channel 是重发的，
               不保留就会被「保存一次就清空所有设备」。 */
            Subscriptions = current.Channel.Subscriptions ?? new List<PushSubscription>(),
        };

        Validate(current);
        Write(user, current);
        return ReadForApi(user);
    }

    /// <summary>只写服务端状态（调度器用）。</summary>
    public void SaveState(string user, PushConfig cfg) => Write(user, cfg);

    public IReadOnlyList<(string User, PushConfig Config)> ReadAll()
    {
        var list = new List<(string, PushConfig)>();
        try
        {
            if (!System.IO.Directory.Exists(Root)) return list;
            foreach (var file in System.IO.Directory.EnumerateFiles(Root, "*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var user = CleanUserName(name);
                list.Add((user, Read(user)));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[push] 枚举推送配置失败：{ex.Message}");
        }
        return list;
    }

    public static string MaskToken(string token)
    {
        var value = (token ?? "").Trim();
        if (value.Length == 0) return "";
        return value.Length <= 4 ? "****" : "****" + value[^4..];
    }

    /* 打码值原样回传 = 用户没改，保留原来的 token；空串 = 明确清空。 */
    private static string MergeToken(string current, string incoming)
    {
        var value = (incoming ?? "").Trim();
        if (value.StartsWith("****", StringComparison.Ordinal)) return current ?? "";
        return value;
    }

    private static string NormalizeType(string? type)
    {
        var value = (type ?? "").Trim().ToLowerInvariant();
        /* 默认给 webpush：新用户最省事的渠道（不用装 App、不用第三方账号）。
           已有配置里写了具体 type 的照旧，不受影响。
           ⚠️ "bark" 必须**显式列出**：它原来是兜底值，改成 webpush 兜底之后，
           漏掉它会让选 Bark 的人静默变成浏览器通知（校验还会报「先绑定设备」）。

           ⚠️ ntfy / serverchan / wechat 仍在白名单里：界面上这三个已经并入
           bark / pushplus / webhook（三者功能完全重复，只会让选择变难），
           但**老配置里存着这些值**。不认它们 = 用户某次更新后保存一次设置，
           渠道就被悄悄改成 webpush，每天的提醒从此发不出去且没人报错。
           收界面选项和收后端能力是两件事，这里只做前者。 */
        return value is "bark" or "ntfy" or "webhook" or "wechat" or "serverchan" or "pushplus" or "webpush"
            ? value
            : "webpush";
    }

    private static string NormalizeStyle(string? style)
    {
        var value = (style ?? "").Trim().ToLowerInvariant();
        return value is "remind" or "digest" ? value : "milestone";
    }

    private static string NormalizeTime(string? time)
    {
        var match = Regex.Match((time ?? "").Trim(), @"^(\d{1,2}):(\d{2})$");
        if (!match.Success) return "20:00";
        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (hour is < 0 or > 23 || minute is < 0 or > 59) return "20:00";
        return $"{hour:00}:{minute:00}";
    }

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        var value = (baseUrl ?? "").Trim();
        return value.Length == 0 ? "" : value.TrimEnd('/');
    }

    private static void Validate(PushConfig cfg)
    {
        var url = cfg.Channel.Url;
        var token = (cfg.Channel.Token ?? "").Trim();
        /* webpush 既不需要地址也不需要令牌 —— 凭据是订阅本身（每台设备一份）。
           它唯一的前置条件是「至少绑了一台设备」，否则启用后每天都发不出去。 */
        if (cfg.Channel.Type == "webpush")
        {
            if (cfg.Enabled && cfg.Channel.Subscriptions.Count == 0)
            {
                throw new InvalidOperationException("启用前请先点「绑定本机」—— 浏览器通知需要至少一台已绑定的设备");
            }
        }
        else
        {
            /* Server酱 / PushPlus 的接口地址是服务商固定的，用户手里只有一串 key：
               地址留空 = 走默认接口（发送端会自动补上），所以这里不要求填地址。 */
            var keyOnly = cfg.Channel.Type is "serverchan" or "pushplus";

            if (keyOnly)
            {
                if (token.Length == 0 && cfg.Enabled)
                {
                    throw new InvalidOperationException(cfg.Channel.Type == "serverchan"
                        ? "启用前请先填写 Server酱的 SendKey"
                        : "启用前请先填写 PushPlus 的 token");
                }
            }
            else if (url.Length == 0)
            {
                if (cfg.Enabled) throw new InvalidOperationException("启用每日提醒前请先填写推送地址");
                return;
            }
        }

        if (url.Length > 0
            && (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException("推送地址必须是 http:// 或 https:// 开头的完整 URL");
        }
        if (cfg.BaseUrl.Length > 0
            && (!Uri.TryCreate(cfg.BaseUrl, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException("「点击跳转」地址必须是 http:// 或 https:// 开头的完整 URL");
        }
    }

    /* ── webpush 订阅的增删 ──────────────────────────────────────────────
       单独开方法而不是让前端 PUT 整个 channel：订阅是「每台设备各一份凭据」，
       走整包保存必然出现「手机保存一次就把电脑那份冲掉」。 */

    /// <summary>登记一台设备。同一个 endpoint 重复提交 = 更新（浏览器会重发同一个）。</summary>
    public object AddSubscription(string user, PushSubscription incoming)
    {
        if (incoming is null || !incoming.IsValid)
        {
            throw new InvalidOperationException("订阅信息不完整（需要 endpoint / p256dh / auth）");
        }
        var cfg = Read(user);
        var endpoint = incoming.Endpoint.Trim();
        var list = cfg.Channel.Subscriptions;
        list.RemoveAll(s => string.Equals(s.Endpoint, endpoint, StringComparison.Ordinal));
        list.Add(new PushSubscription
        {
            Endpoint = endpoint,
            P256dh = incoming.P256dh.Trim(),
            Auth = incoming.Auth.Trim(),
            UserAgent = (incoming.UserAgent ?? "").Trim(),
            CreatedAt = NowText(),
        });
        /* 绑一台就顺手把渠道切成 webpush：用户点「绑定本机」时多半还没选过渠道，
           绑完却因为 type 还是 bark 而发不出去，是最容易踩的坑。 */
        cfg.Channel.Type = "webpush";
        Write(user, cfg);
        return ReadForApi(user);
    }

    /// <summary>按短 id 或完整 endpoint 解除绑定。返回实际删掉了几台。</summary>
    public (object Config, int Removed) RemoveSubscription(string user, string? id, string? endpoint)
    {
        var cfg = Read(user);
        var wantEndpoint = (endpoint ?? "").Trim();
        var wantId = (id ?? "").Trim();
        var removed = cfg.Channel.Subscriptions.RemoveAll(s =>
            (wantEndpoint.Length > 0 && string.Equals(s.Endpoint, wantEndpoint, StringComparison.Ordinal))
            || (wantId.Length > 0 && string.Equals(SubscriptionId(s.Endpoint), wantId, StringComparison.Ordinal)));
        if (removed > 0) Write(user, cfg);
        return (ReadForApi(user), removed);
    }

    /// <summary>发送时发现 404/410（设备已注销 / 浏览器清了数据）→ 顺手删掉。</summary>
    public void DropSubscriptions(string user, IReadOnlyCollection<string> endpoints)
    {
        if (endpoints.Count == 0) return;
        var cfg = Read(user);
        var removed = cfg.Channel.Subscriptions.RemoveAll(s => endpoints.Contains(s.Endpoint));
        if (removed > 0)
        {
            Write(user, cfg);
            Console.WriteLine($"[push] {user}：已自动解除 {removed} 台失效设备的绑定");
        }
    }

    /// <summary>订阅的稳定短 id：端点换了它就变，够前端区分设备用。</summary>
    public static string SubscriptionId(string endpoint)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(endpoint ?? ""));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string HostOf(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "未知";
    }

    /// <summary>UA 只留能认出来的部分：整串太长，界面上也不需要。</summary>
    private static string ShortUserAgent(string ua)
    {
        var value = (ua ?? "").Trim();
        if (value.Length == 0) return "";
        foreach (var (marker, label) in new[]
                 {
                     ("iPhone", "iPhone"), ("iPad", "iPad"), ("Android", "Android"),
                     ("Windows", "Windows"), ("Macintosh", "Mac"), ("Linux", "Linux"),
                 })
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase)) return label;
        }
        return value.Length <= 24 ? value : value[..24];
    }

    private void Write(string user, PushConfig cfg)
    {
        var clean = CleanUserName(user);
        var gate = _locks.GetOrAdd(clean, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            System.IO.Directory.CreateDirectory(Root);
            var path = PathFor(clean);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(cfg, JsonOptions), new UTF8Encoding(false));
                File.Move(temp, path, true);
                RestrictFilePermissions(path);
            }
            finally
            {
                TryDeleteFile(temp);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>从用户数据整包里算今日摘要。只读，一个字节都不写。</summary>
static class PushDigestReader
{
    /// <summary>
    /// SRS 复习间隔。**必须与 public/app.js:180 的 REVIEW_INTERVAL_DAYS 逐字一致**。
    /// 这是整个推送功能唯一的逻辑双份实现：改任何一侧都要同时改另一侧，
    /// 否则推送里的「N 道到期」会和错题本里的数字对不上。
    /// 漂移由 /api/push/preview 兜住 —— 它把服务端算的数字摊开给前端并排显示。
    /// </summary>
    internal static readonly int[] ReviewIntervalDays = { 1, 2, 4, 7, 15, 30 };

    /// <summary>读失败返回 null（调用方据此**不发**，而不是当成「今天还没练」发一条错的）。</summary>
    public static async Task<PushDigest?> ComputeAsync(UserDataStore userData, string user, DateTime now)
    {
        object data;
        try
        {
            (data, _) = await userData.LoadAsync(user);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[push] 读取 {user} 的学习数据失败，本轮跳过：{ex.Message}");
            return null;
        }
        return Compute(data, now);
    }

    public static PushDigest Compute(object? data, DateTime now)
    {
        var root = ToElement(data);
        var digest = new PushDigest();
        var today = now.Date;
        var todayEnd = today.AddDays(1).AddTicks(-1);
        var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // ── 错题本：storage.wrong[qid] = {stage, count, lastReviewAt, wrongAt, at, resolved, ...}
        //    注意它在**顶层**、跨课程；courses[cid].wrong[qid] 只是 true 标记。
        if (TryGet(root, "wrong", out var wrong) && wrong.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in wrong.EnumerateObject())
            {
                var record = prop.Value;
                if (record.ValueKind != JsonValueKind.Object)
                {
                    /* 老数据里可能有布尔值/垃圾值：算「有错题」但算不出到期日，不计入 due。
                       前端 ensureWrongReviewFields 对这类值的处理等价于「今天刚错、明天到期」，
                       所以这里跳过 = 同样的结果。 */
                    digest.WrongTotal++;
                    continue;
                }
                if (Bool(record, "resolved")) continue;
                digest.WrongTotal++;
                var stage = Math.Clamp(Int(record, "stage"), 0, ReviewIntervalDays.Length - 1);
                var anchor = ParseDate(Text(record, "lastReviewAt"))
                             ?? ParseDate(Text(record, "wrongAt"))
                             ?? ParseDate(Text(record, "at"));
                if (anchor is null) continue;
                if (anchor.Value.AddDays(ReviewIntervalDays[stage]) <= todayEnd) digest.Due++;
            }
        }

        // ── 今日活动 + 连续天数：dailyActivity["YYYY-MM-DD"]（只保留 90 天）
        if (TryGet(root, "dailyActivity", out var daily) && daily.ValueKind == JsonValueKind.Object)
        {
            if (daily.TryGetProperty(todayKey, out var day) && day.ValueKind == JsonValueKind.Object)
            {
                digest.DoneToday = Int(day, "total");
                digest.CorrectToday = Int(day, "correct");
                digest.VerifiedToday = Int(day, "verified");
            }
            /* 今天有练就从今天数，没练从**昨天**数 —— 否则每天早上看到的都是「连续 0 天」。 */
            var cursor = digest.DoneToday > 0 ? today : today.AddDays(-1);
            while (digest.Streak < 400)
            {
                var key = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!daily.TryGetProperty(key, out var bucket) || bucket.ValueKind != JsonValueKind.Object) break;
                if (Int(bucket, "total") <= 0) break;
                digest.Streak++;
                cursor = cursor.AddDays(-1);
            }
            // 最近一次练过的日期（断签提醒用）。90 天窗口内找不到就当「很久没练」。
            var probe = today;
            for (var i = 0; i <= 90; i++)
            {
                var key = probe.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (daily.TryGetProperty(key, out var bucket) && bucket.ValueKind == JsonValueKind.Object && Int(bucket, "total") > 0)
                {
                    digest.DaysSinceLast = i;
                    break;
                }
                probe = probe.AddDays(-1);
            }
        }

        // ── 考试计划：plans[cid] = {courseId, examDate, dailyTarget, dailyTargetManual, dailyLog}
        if (TryGet(root, "plans", out var plans) && plans.ValueKind == JsonValueKind.Object)
        {
            foreach (var plan in plans.EnumerateObject())
            {
                if (plan.Value.ValueKind != JsonValueKind.Object) continue;
                var examDate = ParseDate(Text(plan.Value, "examDate"));
                if (examDate is not null)
                {
                    var left = (int)Math.Ceiling((examDate.Value.Date - today).TotalDays);
                    if (left >= 0 && (digest.DaysLeft < 0 || left < digest.DaysLeft)) digest.DaysLeft = left;
                }
                var target = Int(plan.Value, "dailyTarget");
                if (target > 0 && (digest.DailyTarget == 0 || target < digest.DailyTarget)) digest.DailyTarget = target;
            }
        }

        // ── 科目名：profile.lastCourseName
        if (TryGet(root, "profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
        {
            digest.CourseName = Text(profile, "lastCourseName");
        }

        return digest;
    }

    private static JsonElement ToElement(object? data) => data switch
    {
        JsonElement element => element,
        null => JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        _ => JsonSerializer.SerializeToElement(data),
    };

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    private static string Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (value.TryGetInt32(out var number) ? number : (int)Math.Round(value.GetDouble()))
            : 0;

    private static bool Bool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && (value.ValueKind == JsonValueKind.True
               || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n != 0));

    private static DateTime? ParseDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim().Replace('/', '-');
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed;
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed) ? parsed : null;
    }
}

/// <summary>把摘要变成一条通知。返回 null = 今天不发。</summary>
static class PushMessageBuilder
{
    /* 里程碑阈值：7 / 30 / 60 天。
       为什么不放 100：dailyActivity 只保留 90 天（app.js:5918），连续 100 天在数据上不可达，
       写上去就是一个永远不会触发的分支。 */
    private static readonly int[] StreakMilestones = { 7, 30, 60 };

    public static PushMessage? Build(PushConfig cfg, PushDigest d, DateTime now, bool test = false)
    {
        var style = (cfg.Style ?? "milestone").Trim().ToLowerInvariant();
        var prefix = test ? "【测试】" : "";
        var url = BuildUrl(cfg.BaseUrl);

        if (d.DoneToday == 0)
        {
            /* 断签优先：「今天还没开始」对连续 5 天没打开过的人太轻了。 */
            if (d.DaysSinceLast >= 3)
            {
                return new PushMessage(prefix + $"已经 {d.DaysSinceLast} 天没练", Join($"错题堆到 {d.WrongTotal} 道", ExamText(d)), url);
            }
            if (d.Due > 0)
            {
                return new PushMessage(prefix + $"{d.Due} 道错题到期", Join($"预计 {EstimateMinutes(d.Due)} 分钟", ExamText(d)), url);
            }
            return new PushMessage(prefix + "今天还没开始", Join(SuggestText(d), ExamText(d)), url);
        }

        if (style == "remind") return null;   // 只催没练：今天练过了就一句话都不发

        // ── 里程碑（style = milestone / digest）：从稀有到常见
        if (IsStreakMilestone(d.Streak) && cfg.MilestoneStreak < d.Streak)
        {
            return new PushMessage(prefix + $"连续 {d.Streak} 天", Join($"今天 {d.DoneToday} 题", ExamText(d)), url);
        }
        /* 「到期错题清零」本来也在里程碑里，**故意去掉了**：
           一个每天认真练的人每天都能把到期清零，于是它退化成每天必发的第二条消息，
           正是这个方案要避免的通知疲劳。留「单日全对」这种真正稀有的。 */
        if (d.VerifiedToday >= 20 && d.CorrectToday == d.VerifiedToday)
        {
            return new PushMessage(prefix + $"今天 {d.VerifiedToday} 题全对", Join($"连续 {d.Streak} 天", ExamText(d)), url);
        }
        if (style == "digest")
        {
            return new PushMessage(prefix + $"今天 {d.DoneToday} 题", Join($"正确率 {RateText(d)} · 连续 {d.Streak} 天", ExamText(d)), url);
        }
        return null;
    }

    public static bool IsStreakMilestone(int streak) => Array.IndexOf(StreakMilestones, streak) >= 0;

    /// <summary>通知里「点开跳到哪里」的绝对地址；没填就是空串（渠道会忽略）。</summary>
    public static string UrlFor(string baseUrl) => BuildUrl(baseUrl);

    private static string BuildUrl(string baseUrl)
    {
        var value = (baseUrl ?? "").Trim().TrimEnd('/');
        return value.Length == 0 ? "" : value + "/?from=push";
    }

    private static string SuggestText(PushDigest d)
        => d.DailyTarget > 0 ? $"建议 {d.DailyTarget} 题" : "练几道保持手感";

    private static string ExamText(PushDigest d)
    {
        if (d.DaysLeft < 0) return "";
        if (d.DaysLeft == 0) return "今天考试";
        return $"距考试 {d.DaysLeft} 天";
    }

    private static string RateText(PushDigest d)
        => d.VerifiedToday > 0 ? $"{Math.Round(d.CorrectToday * 100.0 / d.VerifiedToday)}%" : "—";

    private static int EstimateMinutes(int count) => Math.Max(1, (int)Math.Ceiling(count * 0.5));

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>三个渠道适配器。都只是「拼一个 HTTP 请求」，没有 SDK 依赖。</summary>
static class PushSenders
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public static async Task<(bool Ok, string Error)> SendAsync(PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var type = (channel?.Type ?? "bark").Trim().ToLowerInvariant();
        var url = (channel?.Url ?? "").Trim();
        /* Server酱 / PushPlus：接口地址是服务商固定的，用户只填 key。地址留空就补默认值。 */
        if (url.Length == 0)
        {
            if (type == "serverchan") url = "https://sctapi.ftqq.com";
            else if (type == "pushplus") url = "https://www.pushplus.plus/send";
        }
        if (url.Length == 0) return (false, "推送地址为空");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (false, "推送地址必须是 http/https 开头的完整 URL");
        }
        try
        {
            return type switch
            {
                "ntfy" => await SendNtfyAsync(uri, channel!, message, ct),
                "webhook" => await SendWebhookAsync(uri, channel!, message, ct),
                "wechat" => await SendWeChatAsync(uri, message, ct),
                "serverchan" => await SendServerChanAsync(uri, channel!, message, ct),
                "pushplus" => await SendPushPlusAsync(uri, channel!, message, ct),
                _ => await SendBarkAsync(uri, message, ct),
            };
        }
        catch (TaskCanceledException)
        {
            return (false, "推送超时（10 秒无响应）");
        }
        catch (HttpRequestException ex)
        {
            return (false, "推送请求失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            return (false, "推送失败：" + ex.Message);
        }
    }

    /* Bark：POST 到用户给的地址（已含 device key），body 是 JSON。
       ⚠️ Bark 的 HTTP 状态码恒为 200，真正的结果在 body 的 code 字段里。 */
    private static async Task<(bool, string)> SendBarkAsync(Uri uri, PushMessage message, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = message.Title,
            ["body"] = message.Body,
            ["group"] = "云习题库",
            ["level"] = "active",
        };
        if (message.Url.Length > 0) payload["url"] = message.Url;
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(uri, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (false, $"Bark 返回 HTTP {(int)response.StatusCode}：{Short(text)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var number)
                && number != 200)
            {
                return (false, $"Bark 返回 code {number}：{Short(text)}");
            }
        }
        catch (JsonException)
        {
            // 自建 Bark / 中间层可能回纯文本，HTTP 2xx 就算成功
        }
        return (true, "");
    }

    /* ntfy：用 **JSON 发布**（POST 到用户填的 https://ntfy.sh/主题名）。
       ⚠️ 不能用 header 传标题：.NET 的 HttpClient **拒绝非 ASCII 的 header 值**，
       中文标题会直接抛「Request headers must contain only ASCII characters」——
       第一版就是拿 `Title` header 传的，实测 100% 失败（HTTP 请求都没发出去）。
       JSON body 没有这个限制。
       topic 不写进 JSON：URL 路径里已经有主题，ntfy 在 body 缺 topic 时用路径里那个。 */
    private static async Task<(bool, string)> SendNtfyAsync(Uri uri, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = message.Title,
            ["message"] = message.Body,
            ["priority"] = 3,
            ["tags"] = new[] { "books" },
        };
        if (message.Url.Length > 0) payload["click"] = message.Url;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(channel.Token)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + channel.Token.Trim());
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode
            ? (true, "")
            : (false, $"ntfy 返回 HTTP {(int)response.StatusCode}：{Short(text)}");
    }

    /* 企业微信群机器人：POST 到群机器人 webhook，body 形状固定。
       ⚠️ 和 Bark 一样，HTTP 恒 200，真正的结果在 body 的 errcode 里。
       注意它进的是**企业微信群**，不是个人微信 —— 要发到个人微信得用下面两个。 */
    private static async Task<(bool, string)> SendWeChatAsync(Uri uri, PushMessage message, CancellationToken ct)
    {
        var content = message.Url.Length > 0
            ? $"{message.Title}\n{message.Body}\n{message.Url}"
            : $"{message.Title}\n{message.Body}";
        var payload = new Dictionary<string, object?>
        {
            ["msgtype"] = "text",
            ["text"] = new Dictionary<string, object?> { ["content"] = content },
        };
        using var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(uri, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (false, $"企业微信返回 HTTP {(int)response.StatusCode}：{Short(text)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("errcode", out var code)
                && code.TryGetInt32(out var number)
                && number != 0)
            {
                return (false, $"企业微信返回 errcode {number}：{Short(text)}");
            }
        }
        catch (JsonException)
        {
            // 自建转发层可能回纯文本，HTTP 2xx 就算成功
        }
        return (true, "");
    }

    /* Server酱（个人微信）：POST 表单到 <base>/<SendKey>.send，code==0 才算成功。
       免费版每天 5 条 —— 一天一条够用。base 可换成 https://<uid>.push.ft07.com/send。 */
    private static async Task<(bool, string)> SendServerChanAsync(Uri uri, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var key = (channel.Token ?? "").Trim();
        if (key.Length == 0) return (false, "Server酱缺少 SendKey");
        var endpoint = uri.AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(key) + ".send";
        var form = new Dictionary<string, string>
        {
            ["title"] = message.Title,
            ["desp"] = message.Url.Length > 0 ? $"{message.Body}\n\n[点这里打开]({message.Url})" : message.Body,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (false, $"Server酱返回 HTTP {(int)response.StatusCode}：{Short(text)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var code)
                && code.TryGetInt32(out var number)
                && number != 0)
            {
                return (false, $"Server酱返回 code {number}：{Short(text)}");
            }
        }
        catch (JsonException) { }
        return (true, "");
    }

    /* PushPlus（个人微信）：POST JSON，code==200 才算成功。
       用 template=txt（纯文本），避免正文里的 < > & 需要再做一次 HTML 转义。 */
    private static async Task<(bool, string)> SendPushPlusAsync(Uri uri, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var token = (channel.Token ?? "").Trim();
        if (token.Length == 0) return (false, "PushPlus 缺少 token");
        var payload = new Dictionary<string, object?>
        {
            ["token"] = token,
            ["title"] = message.Title,
            ["content"] = message.Url.Length > 0 ? $"{message.Body}\n{message.Url}" : message.Body,
            ["template"] = "txt",
        };
        using var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(uri, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (false, $"PushPlus 返回 HTTP {(int)response.StatusCode}：{Short(text)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var code)
                && code.TryGetInt32(out var number)
                && number != 200)
            {
                return (false, $"PushPlus 返回 code {number}：{Short(text)}");
            }
        }
        catch (JsonException) { }
        return (true, "");
    }

    /* 通用 webhook：模板里的 {{title}} / {{body}} / {{url}} 会被**JSON 转义后**填入，
       所以模板本身可以直接写 JSON：
         {"msgtype":"text","text":{"content":"{{title}}\n{{body}}\n{{url}}"}}
       一条实现覆盖企业微信 / 飞书 / Server酱 / PushDeer / Telegram Bot。 */
    private static async Task<(bool, string)> SendWebhookAsync(Uri uri, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var template = (channel.Template ?? "").Trim();
        var body = template.Length == 0
            ? JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["title"] = message.Title,
                ["body"] = message.Body,
                ["url"] = message.Url,
            })
            : template
                .Replace("{{title}}", JsonEscape(message.Title), StringComparison.Ordinal)
                .Replace("{{body}}", JsonEscape(message.Body), StringComparison.Ordinal)
                .Replace("{{url}}", JsonEscape(message.Url), StringComparison.Ordinal);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(uri, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode
            ? (true, "")
            : (false, $"webhook 返回 HTTP {(int)response.StatusCode}：{Short(text)}");
    }

    private static string JsonEscape(string value)
    {
        var serialized = JsonSerializer.Serialize(value ?? "");
        return serialized.Length >= 2 ? serialized[1..^1] : "";
    }

    private static string Short(string text)
    {
        var value = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= 200 ? value : value[..200] + "…";
    }
}

/// <summary>
/// Web Push 的 VAPID 密钥对（RFC 8292）。全站一份，第一次用到时生成并落盘。
/// 存在 userdata/push/_server/ 这个**子目录**里：userdata 顶层的 *.json 就是账号列表
/// （见 /api/admin/users），push 已经是子目录，再往下一层连枚举都碰不到。
/// </summary>
sealed class VapidKeyStore
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly string _subject;
    private string _public = "";
    private string _private = "";
    private bool _loaded;

    public VapidKeyStore(AppPaths paths, IConfiguration configuration)
    {
        _paths = paths;
        /* VAPID 的 sub 按规范是联系方式（mailto: 或 https:）。推送服务不校验内容，
           但留空不合规范，给一个明确无害的默认值。 */
        var subject = (configuration["App:VapidSubject"] ?? "").Trim();
        _subject = subject.Length > 0 ? subject : "mailto:admin@localhost";
    }

    private string Dir => Path.Combine(_paths.UserDataRoot, "push", "_server");
    private string FilePath => Path.Combine(Dir, "vapid.json");

    /// <summary>给浏览器的公钥（base64url，65 字节未压缩点）。</summary>
    public string PublicKey
    {
        get
        {
            Ensure();
            lock (_gate) return _public;
        }
    }

    /// <summary>RFC 8292 的授权头：<c>vapid t=&lt;jwt&gt;, k=&lt;公钥&gt;</c>。</summary>
    public string AuthorizationHeader(Uri endpoint)
    {
        Ensure();
        string publicKey;
        string signingInput;
        byte[] signature;
        lock (_gate)
        {
            publicKey = _public;
            var aud = endpoint.GetLeftPart(UriPartial.Authority);
            var header = Base64Url(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
            var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["aud"] = aud,
                ["exp"] = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(),
                ["sub"] = _subject,
            });
            var payload = Base64Url(Encoding.UTF8.GetBytes(payloadJson));
            signingInput = header + "." + payload;
            using var signer = CreateSigner();
            /* JWS 要的是 r||s 的定长拼接（64 字节），不是 DER —— 默认格式浏览器会拒。
               12 小时有效期：官方建议不超过 24 小时，每次发送现签，不复用。 */
            signature = signer.SignData(Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        return $"vapid t={signingInput}.{Base64Url(signature)}, k={publicKey}";
    }

    private void Ensure()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(FilePath))
                {
                    var body = File.ReadAllText(FilePath, Encoding.UTF8);
                    var saved = JsonSerializer.Deserialize<VapidFile>(body, PushConfigStore.JsonOptions);
                    if (saved is not null && saved.PublicKey.Length > 0 && saved.PrivateKey.Length > 0)
                    {
                        _public = saved.PublicKey;
                        _private = saved.PrivateKey;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[push] 读取 VAPID 密钥失败，重新生成：{ex.Message}");
            }
            Generate();
        }
    }

    private void Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(true);
        var pub = new byte[65];
        pub[0] = 0x04;
        p.Q.X!.CopyTo(pub, 1);
        p.Q.Y!.CopyTo(pub, 33);
        _public = Base64Url(pub);
        _private = Base64Url(p.D!);
        try
        {
            System.IO.Directory.CreateDirectory(Dir);
            var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp,
                JsonSerializer.Serialize(new VapidFile { PublicKey = _public, PrivateKey = _private, CreatedAt = NowText() },
                    PushConfigStore.JsonOptions),
                new UTF8Encoding(false));
            File.Move(temp, FilePath, true);
            RestrictFilePermissions(FilePath);
        }
        catch (Exception ex)
        {
            /* 写不下去也要能用：进程活着期间推送照发。只是重启后公钥会变，
               旧订阅随之失效（浏览器下次打开会重新订阅），日志留痕便于排查。 */
            Console.WriteLine($"[push] 保存 VAPID 密钥失败（重启后已绑定设备需重新绑定）：{ex.Message}");
        }
    }

    private ECDsa CreateSigner()
    {
        var pub = Base64UrlDecode(_public);
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64UrlDecode(_private),
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var text = (value ?? "").Trim().Replace('-', '+').Replace('_', '/');
        if (text.Length == 0) return Array.Empty<byte>();
        return Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
    }

    private sealed class VapidFile
    {
        public string PublicKey { get; set; } = "";
        public string PrivateKey { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }
}

/// <summary>
/// Web Push 发送端（RFC 8030 传输 / 8291 消息加密 / 8292 VAPID）。
/// .NET 没有内置实现，这里手写两条：
///   · VAPID —— 用 ES256 签一个 JWT 放进 Authorization 头，向推送服务声明「我是这个站」；
///   · 加密 —— 与设备公钥做 ECDH，HKDF 派生出 AES-128-GCM 的 key/nonce，按 RFC 8188 组包。
/// 推送服务（FCM / Mozilla / Apple）只转发密文：看得到发给谁，看不到内容。
/// </summary>
sealed class WebPushSender
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly VapidKeyStore _keys;
    private readonly PushConfigStore _store;

    public WebPushSender(VapidKeyStore keys, PushConfigStore store)
    {
        _keys = keys;
        _store = store;
    }

    /// <summary>浏览器 subscribe() 要的公钥。</summary>
    public string PublicKey => _keys.PublicKey;

    /// <summary>逐台推。全部失败才算失败（一个人手机绑着、电脑关着，不该算发送失败）。</summary>
    public async Task<(bool Ok, string Error)> SendAsync(string user, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var subs = channel?.Subscriptions ?? new List<PushSubscription>();
        if (subs.Count == 0) return (false, "还没有绑定设备");
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["title"] = message.Title,
            ["body"] = message.Body,
            ["url"] = message.Url,
        }));

        var delivered = 0;
        var dead = new List<string>();
        var errors = new List<string>();
        foreach (var sub in subs)
        {
            var (ok, error, gone) = await SendOneAsync(sub, plaintext, ct);
            if (ok) { delivered++; continue; }
            if (gone) { dead.Add(sub.Endpoint); continue; }
            errors.Add(error);
        }
        _store.DropSubscriptions(user, dead);
        if (delivered > 0) return (true, "");
        if (errors.Count > 0) return (false, string.Join("；", errors.Take(3)));
        return (false, "所有设备都已失效，已自动解除绑定");
    }

    private async Task<(bool Ok, string Error, bool Gone)> SendOneAsync(PushSubscription sub, byte[] plaintext, CancellationToken ct)
    {
        if (!Uri.TryCreate(sub.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return (false, "订阅地址无效", true);
        }
        try
        {
            var body = Encrypt(sub, plaintext, out _);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
            /* 这三个都是**内容头**，挂到 request.Headers 上会被拒。 */
            request.Content.Headers.TryAddWithoutValidation("Content-Encoding", "aes128gcm");
            request.Headers.TryAddWithoutValidation("TTL", "86400");
            request.Headers.TryAddWithoutValidation("Urgency", "normal");
            request.Headers.TryAddWithoutValidation("Authorization", _keys.AuthorizationHeader(endpoint));
            /* 有些推送服务或前置代理（Cloudflare 一类）对空 User-Agent 直接回 402/403。
               实测 httpbingo 就是「无 UA → 402」。加上标识既避开这类机器人策略，
               也方便对方在日志里认出调用方。 */
            request.Headers.TryAddWithoutValidation("User-Agent", "YunxiTiku-Push/1.0");
            using var response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return (true, "", false);
            var status = (int)response.StatusCode;
            /* 404 / 410：设备已注销、浏览器清了数据、或卸载了站点。这条订阅永远不可能
               再成功，留着只会让「发送成功」永远为假 —— 直接删掉，前端列表也跟着消失。 */
            if (status is 404 or 410) return (false, "", true);
            var text = await response.Content.ReadAsStringAsync(ct);
            return (false, $"推送服务返回 HTTP {status}：{Shorten(text)}", false);
        }
        catch (TaskCanceledException)
        {
            return (false, "推送超时（10 秒无响应）", false);
        }
        catch (HttpRequestException ex)
        {
            return (false, "推送请求失败：" + ex.Message, false);
        }
        catch (Exception ex)
        {
            return (false, "推送失败：" + ex.Message, false);
        }
    }

    /// <summary>RFC 8291（消息加密）+ RFC 8188（aes128gcm 内容编码）。</summary>
    private static byte[] Encrypt(PushSubscription sub, byte[] plaintext, out byte[] asPublic)
    {
        var uaPublic = VapidKeyStore.Base64UrlDecode(sub.P256dh);
        var authSecret = VapidKeyStore.Base64UrlDecode(sub.Auth);
        if (uaPublic.Length != 65) throw new InvalidOperationException("设备公钥格式不对");
        if (authSecret.Length != 16) throw new InvalidOperationException("设备认证密钥格式不对");

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var asParams = ecdh.ExportParameters(false);
        asPublic = new byte[65];
        asPublic[0] = 0x04;
        asParams.Q.X!.CopyTo(asPublic, 1);
        asParams.Q.Y!.CopyTo(asPublic, 33);

        using var uaKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var sharedSecret = ecdh.DeriveRawSecretAgreement(uaKey.PublicKey);

        /* RFC 8291 §3.4：IKM = HKDF(salt = auth_secret,
                                     ikm  = ECDH 共享密钥,
                                     info = "WebPush: info\0" || ua_public || as_public) */
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);

        var salt = RandomNumberGenerator.GetBytes(16);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        // 明文末尾补 0x02 = RFC 8188 的「最后一条记录」分隔符。
        var padded = Concat(plaintext, new byte[] { 0x02 });
        // GCM 的 16 字节认证标签接在密文后面（RFC 8188 就是这么定义的），
        // 所以缓冲区多留 16 字节，用 span 重载把标签写进去 —— 数组重载要传 tag:null，
        // 在可空引用类型下是个 CS8625 警告。
        var ciphertext = new byte[padded.Length + 16];
        using (var gcm = new AesGcm(cek, 16))
        {
            gcm.Encrypt(nonce, padded, ciphertext.AsSpan(0, padded.Length), ciphertext.AsSpan(padded.Length));
        }

        // 正文 = salt(16) || rs(4) || idlen(1) || keyid(as_public, 65) || 密文
        var body = new byte[16 + 4 + 1 + 65 + ciphertext.Length];
        salt.CopyTo(body, 0);
        var rsBytes = BitConverter.GetBytes(4096);
        if (BitConverter.IsLittleEndian) Array.Reverse(rsBytes);
        rsBytes.CopyTo(body, 16);
        body[20] = 65;
        asPublic.CopyTo(body, 21);
        ciphertext.CopyTo(body, 86);
        return body;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static string Shorten(string text)
    {
        var value = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= 200 ? value : value[..200] + "…";
    }
}

/// <summary>「是不是 webpush」这一个分支收在一处：调度器和「发送测试」共用。</summary>
static class PushDelivery
{
    public static Task<(bool Ok, string Error)> SendAsync(WebPushSender webPush, string user, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var type = (channel?.Type ?? "").Trim().ToLowerInvariant();
        return type == "webpush"
            ? webPush.SendAsync(user, channel!, message, ct)
            : PushSenders.SendAsync(channel!, message, ct);
    }
}

/// <summary>
/// 每 30 秒扫一遍 userdata/push/*.json，到点就发。
/// 判据全部在 RunOnceAsync 里：开关 → 今天是否已发 → 是否到点 → 是否还在宽限期 → 退避。
/// </summary>
sealed class PushScheduler : BackgroundService
{
    private readonly PushConfigStore _store;
    private readonly UserDataStore _userData;
    private readonly WebPushSender _webPush;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PushScheduler(PushConfigStore store, UserDataStore userData, WebPushSender webPush)
    {
        _store = store;
        _userData = userData;
        _webPush = webPush;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            /* 启动后先等 25 秒：让题库预热、章节映射自检先跑完，别在启动瞬间抢磁盘 IO。
               顺便也避免「刚部署完重启」触发一轮立刻发送。 */
            await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        Console.WriteLine("[push] 调度器已启动：每 30 秒检查一次");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 一轮失败不能让调度器停摆 —— 明天还得发。
                Console.WriteLine($"[push] 调度轮次异常：{ex}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 跑一轮。只有调度器调用 —— 之前还有个「管理员手动触发」的入口，连同
    /// ignoreTime / onlyUser 两个参数一起删了（见 /api/push/run 处的说明）。
    /// </summary>
    public async Task<PushRunReport> RunOnceAsync(CancellationToken ct)
    {
        var report = new PushRunReport();
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.Now;
            var today = now.Date;
            var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            foreach (var (user, cfg) in _store.ReadAll())
            {
                if (!cfg.Enabled) { report.Skipped++; continue; }

                // 今天已经发过就跳过 —— 调度器每 30 秒来一次，这里是唯一的防重发闸门。
                if (cfg.LastSentDate == todayKey) { report.Skipped++; continue; }

                {
                    if (!TryParseTime(cfg.Time, out var hour, out var minute)) { report.Skipped++; continue; }
                    var target = today.AddHours(hour).AddMinutes(minute);
                    if (now < target) { report.Skipped++; continue; }
                    /* 宽限期：容器半夜重启 / 白天关机，只要在 time + graceHours 内起来就补一条；
                       超过就不补了，免得晚上十一点弹一句「今天还没练」。 */
                    if ((now - target).TotalHours > Math.Clamp(cfg.GraceHours, 0, 23)) { report.Skipped++; continue; }
                    if (cfg.NextRetryAt.Length > 0
                        && DateTime.TryParse(cfg.NextRetryAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var retryAt)
                        && now < retryAt)
                    {
                        report.Skipped++;
                        continue;
                    }
                }

                var digest = await PushDigestReader.ComputeAsync(_userData, user, now);
                if (digest is null)
                {
                    report.Failed++;
                    AddDetail(report, $"{user}：读取学习数据失败，本轮跳过");
                    continue;
                }

                // 断签了 → 连续天数的里程碑重新计。
                if (digest.Streak < cfg.MilestoneStreak) cfg.MilestoneStreak = 0;

                var message = PushMessageBuilder.Build(cfg, digest, now);
                if (message is null) { report.Skipped++; continue; }

                var (ok, error) = await PushDelivery.SendAsync(_webPush, user, cfg.Channel, message, ct);
                if (ok)
                {
                    cfg.LastSentDate = todayKey;
                    cfg.LastSentAt = NowText();
                    cfg.LastResult = "ok";
                    cfg.LastError = "";
                    cfg.FailCount = 0;
                    cfg.NextRetryAt = "";
                    if (PushMessageBuilder.IsStreakMilestone(digest.Streak))
                    {
                        cfg.MilestoneStreak = Math.Max(cfg.MilestoneStreak, digest.Streak);
                    }
                    report.Sent++;
                    AddDetail(report, $"{user}：已发送「{message.Title}」");
                    // 一天一条，写一行日志便于事后核对「到底发了没」。
                    Console.WriteLine($"[push] 已发送 {user}：{message.Title} / {message.Body}");
                }
                else
                {
                    /* 失败**不推进** lastSentDate，但要退避：否则每 30 秒重试一次，
                       渠道挂了就是一天 2800 次请求。 */
                    cfg.FailCount++;
                    cfg.LastResult = "fail";
                    cfg.LastError = error;
                    var delayMinutes = Math.Min(Math.Pow(2, Math.Min(cfg.FailCount, 5)), 32);
                    cfg.NextRetryAt = now.AddMinutes(delayMinutes).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    report.Failed++;
                    AddDetail(report, $"{user}：发送失败 —— {error}");
                }
                _store.SaveState(user, cfg);
            }
        }
        finally
        {
            _gate.Release();
        }
        return report;
    }

    private static void AddDetail(PushRunReport report, string line)
    {
        if (report.Details.Count < 50) report.Details.Add(line);
    }

    private static bool TryParseTime(string? text, out int hour, out int minute)
    {
        hour = 20;
        minute = 0;
        var match = Regex.Match((text ?? "").Trim(), @"^(\d{1,2}):(\d{2})$");
        if (!match.Success) return false;
        hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }
}

