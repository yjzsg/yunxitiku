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
