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
