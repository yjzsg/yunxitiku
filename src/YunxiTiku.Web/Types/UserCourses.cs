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
