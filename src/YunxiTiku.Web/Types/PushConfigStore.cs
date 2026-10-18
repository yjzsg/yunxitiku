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
