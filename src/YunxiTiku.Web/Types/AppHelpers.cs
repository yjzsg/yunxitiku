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
