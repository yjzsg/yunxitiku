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

/* 限长读取请求体（UTF-8）：超限返回 Body=null，由调用方回 400。
   为什么要限长：客户端会把保存请求 gzip 压缩后再发（约 1/10 体积），
   请求体经 UseRequestDecompression 还原后才到这里，压缩比可以被构造成任意大。
   ⚠️ Content-Encoding 与实际内容不符时，解压流会在读取时抛异常 ——
   那是**客户端请求体的问题**，不是服务器故障，要回 400 而不是 500。 */
static async Task<(string? Body, string? Error)> ReadBodyWithLimitAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
{
    try
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read <= 0) break;
            if (buffer.Length + read > maxBytes) return (null, null);
            buffer.Write(chunk, 0, read);
        }
        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), null);
    }
    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
    {
        return (null, "请求体解压失败：Content-Encoding 与实际内容不符");
    }
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
    var (body, readError) = await ReadBodyWithLimitAsync(request.Body, 32 * 1024 * 1024, context.RequestAborted);
    if (readError is not null) return Results.Json(new { ok = false, error = readError }, statusCode: 400);
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
