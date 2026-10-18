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
