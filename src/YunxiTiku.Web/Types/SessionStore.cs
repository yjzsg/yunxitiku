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
