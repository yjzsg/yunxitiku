using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using static AppHelpers;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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
});
builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<AuthStore>();
builder.Services.AddSingleton<QuestionBank>();

var app = builder.Build();
var paths = app.Services.GetRequiredService<AppPaths>();
Directory.CreateDirectory(paths.UserDataRoot);
Directory.CreateDirectory(paths.DataAssetsRoot);
Directory.CreateDirectory(Path.GetDirectoryName(paths.SqlitePath) ?? paths.BaseRoot);
QuestionBank.EnsureDatabase(paths.SqlitePath);

if (app.Configuration.GetValue<bool>("App:EnableHttpsRedirect"))
{
    app.UseHttpsRedirection();
}

var publicFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(paths.PublicRoot);
app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = publicFiles,
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = publicFiles,
});

app.MapGet("/", () => Results.File(Path.Combine(paths.PublicRoot, "index.html"), "text/html; charset=utf-8"));

app.MapGet("/api/health", () => Results.Json(new
{
    ok = true,
    sqlite = File.Exists(paths.SqlitePath),
    tables = QuestionBank.TryReadTableCounts(paths.SqlitePath),
    sqlitePath = paths.SqlitePath,
    publicRoot = paths.PublicRoot,
    userDataRoot = paths.UserDataRoot,
    assetsRoot = paths.DataAssetsRoot,
    assetSample = paths.FindAssetSample()
}));

app.MapGet("/api/users", (AuthStore auth) =>
{
    var users = Directory.GetFiles(paths.UserDataRoot, "*.json")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => CleanUserName(name!))
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (!users.Any(name => name.Equals("admin", StringComparison.OrdinalIgnoreCase))) users.Insert(0, "admin");
    return Results.Json(users.Select(name => new
    {
        name,
        disabled = auth.IsDisabled(name)
    }));
});

app.MapPost("/api/auth/login", async (HttpRequest request, AuthStore auth) =>
{
    var body = await ReadJsonBody(request);
    var user = CleanUserName(GetBodyString(body, "user"));
    var password = GetBodyString(body, "password");
    var profile = auth.Get(user);
    if (auth.IsDisabled(user)) return Results.Json(new { ok = false, error = "账号已停用，请联系管理员" }, statusCode: 403);
    if (profile is null)
    {
        if (!user.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { ok = false, error = "账号不存在，请联系管理员添加" }, statusCode: 404);
        }
        if (password != paths.DefaultLoginPassword)
        {
            return Results.Json(new { ok = false, error = "密码不正确" }, statusCode: 401);
        }
        return Results.Json(new { ok = true, user, mustChangePassword = true });
    }
    if (!auth.Verify(password, profile)) return Results.Json(new { ok = false, error = "密码不正确" }, statusCode: 401);
    return Results.Json(new { ok = true, user, mustChangePassword = auth.MustChangePassword(profile) });
});

app.MapPost("/api/auth/change-password", async (HttpRequest request, AuthStore auth) =>
{
    var body = await ReadJsonBody(request);
    var user = CleanUserName(GetBodyString(body, "user"));
    var oldPassword = GetBodyString(body, "oldPassword");
    var newPassword = GetBodyString(body, "newPassword");
    if (newPassword.Length < 6) return Results.Json(new { ok = false, error = "新密码至少 6 位" }, statusCode: 400);
    var profile = auth.Get(user);
    var oldOk = profile is null ? oldPassword == paths.DefaultLoginPassword : auth.Verify(oldPassword, profile);
    if (!oldOk) return Results.Json(new { ok = false, error = "原密码不正确" }, statusCode: 401);
    var next = auth.CreateProfile(newPassword);
    next["mustChangePassword"] = false;
    auth.Set(user, next);
    auth.Save();
    return Results.Json(new { ok = true, user });
});

app.MapGet("/api/user/load", (string? user) =>
{
    var clean = CleanUserName(user);
    var path = UserDataPath(paths, clean);
    if (!File.Exists(path)) return Results.Json(new { user = clean, data = new Dictionary<string, object?>() });
    try
    {
        var data = JsonSerializer.Deserialize<object>(File.ReadAllText(path, Encoding.UTF8)) ?? new Dictionary<string, object?>();
        return Results.Json(new { user = clean, data });
    }
    catch
    {
        return Results.Json(new { user = clean, data = new Dictionary<string, object?>() });
    }
});

app.MapPost("/api/user/save", async (HttpRequest request, string? user) =>
{
    var clean = CleanUserName(user);
    using var reader = new StreamReader(request.Body, request.ContentType?.Contains("charset", StringComparison.OrdinalIgnoreCase) == true ? Encoding.UTF8 : Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    if (body.Length > 2 * 1024 * 1024) return Results.Json(new { ok = false, error = "user data too large" }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(body)) body = "{}";
    JsonSerializer.Deserialize<object>(body);
    Directory.CreateDirectory(paths.UserDataRoot);
    await File.WriteAllTextAsync(UserDataPath(paths, clean), body, Encoding.UTF8);
    return Results.Json(new { ok = true, user = clean, savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
});

app.MapPost("/api/admin/user-action", async (HttpRequest request, string? user, AuthStore auth) =>
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

    Directory.CreateDirectory(paths.UserDataRoot);
    var profile = auth.Get(target);
    if (action == "disable")
    {
        profile ??= auth.CreateProfile(paths.DefaultLoginPassword);
        profile["disabled"] = true;
        profile["disabledAt"] = NowText();
        auth.Set(target, profile);
        auth.Save();
        return Results.Json(new { ok = true, user = target, disabled = true });
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
        auth.Set(target, profile);
        auth.Save();
        await File.WriteAllTextAsync(UserDataPath(paths, target), "{}", Encoding.UTF8);
        return Results.Json(new { ok = true, user = target, created = true });
    }
    if (action == "reset-password")
    {
        profile = auth.CreateProfile(paths.DefaultLoginPassword);
        profile["mustChangePassword"] = true;
        profile["resetAt"] = NowText();
        auth.Set(target, profile);
        auth.Save();
        return Results.Json(new { ok = true, user = target, resetPassword = true });
    }
    if (action == "clear-data")
    {
        await File.WriteAllTextAsync(UserDataPath(paths, target), "{}", Encoding.UTF8);
        return Results.Json(new { ok = true, user = target, cleared = true });
    }
    if (action == "delete")
    {
        var path = UserDataPath(paths, target);
        if (File.Exists(path)) File.Delete(path);
        auth.Remove(target);
        auth.Save();
        return Results.Json(new { ok = true, user = target, deleted = true });
    }
    return Results.Json(new { ok = false, error = "unknown action" }, statusCode: 400);
});

app.MapPost("/api/admin/update-bank", (string? user, int? courseId) =>
{
    var admin = CleanUserName(user);
    if (!admin.Equals("admin", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
    }
    var results = courseId.HasValue && courseId.Value > 0
        ? new[] { new { courseId = courseId.Value, chapters = 0, subjects = 0, reserved = true } }
        : Array.Empty<object>();
    return Results.Json(new
    {
        ok = true,
        message = "Docker 版已预留更新接口。请在管理员数据管理中上传题库包，或直接替换挂载目录里的 data/question-bank.db。",
        results
    });
});

app.MapGet("/api/admin/data/status", (string? user) =>
{
    if (!IsAdmin(user)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
    return Results.Json(AdminDataTransfer.GetStatus(paths));
});

app.MapGet("/api/admin/data/download", (HttpContext context, string? user, string? type) =>
{
    if (!IsAdmin(user)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
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
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
    }
});

app.MapPost("/api/admin/data/upload", async (HttpRequest request, string? user, string? type, AuthStore auth) =>
{
    if (!IsAdmin(user)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
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
            : AdminDataTransfer.ImportUserData(paths, auth, tempFile, originalName);
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
    }
    finally
    {
        TryDeleteFile(tempFile);
    }
});

app.MapGet("/api/courses", (QuestionBank bank, string? q, string? available) =>
    Results.Json(bank.GetCourses(q ?? "", IsTruthy(available))));

app.MapGet("/api/chapters", (QuestionBank bank, int courseId) =>
    Results.Json(bank.GetChapters(courseId)));

app.MapGet("/api/types", (QuestionBank bank, int courseId, int? chapterId, string? chapterIds) =>
    Results.Json(bank.GetTypes(courseId, chapterId, ParseIdList(chapterIds))));

app.MapGet("/api/questions", (QuestionBank bank, int courseId, int? chapterId, string? chapterIds, int? typeId, string? q, string? order, int? limit, string? ids) =>
    Results.Json(bank.GetQuestions(courseId, chapterId, ParseIdList(chapterIds), typeId, q ?? "", order ?? "", limit, ParseIdList(ids))));

app.MapGet("/api/question", (QuestionBank bank, int id) =>
{
    var question = bank.GetQuestion(id);
    return question is null ? Results.Json(new { error = "question not found" }, statusCode: 404) : Results.Json(question);
});

app.MapGet("/assets/{**relative}", (string relative) =>
{
    relative = Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);
    if (relative.Contains("..")) return Results.Json(new { error = "bad path" }, statusCode: 400);
    var path = Path.Combine(paths.DataAssetsRoot, relative);
    if (!File.Exists(path)) return Results.Json(new { error = "asset not found" }, statusCode: 404);
    return Results.File(path, ContentType(path));
});

app.MapFallback(() => Results.File(Path.Combine(paths.PublicRoot, "index.html"), "text/html; charset=utf-8"));

app.Run();
return 0;

internal static class AppHelpers
{
internal static async Task<Dictionary<string, object?>> ReadJsonBody(HttpRequest request)
{
    var data = await JsonSerializer.DeserializeAsync<Dictionary<string, object?>>(request.Body);
    return data ?? new Dictionary<string, object?>();
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

internal static bool IsAdmin(string? value) => CleanUserName(value).Equals("admin", StringComparison.OrdinalIgnoreCase);

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
internal static string Timestamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

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

static class AdminDataTransfer
{
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
                users = userRoot is null ? 0 : Directory.EnumerateFiles(userRoot.FullName, "*.json", SearchOption.TopDirectoryOnly).Count(),
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
        var tempZip = Path.Combine(Path.GetTempPath(), $"yunxi-bank-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(paths.SqlitePath, "question-bank.db", CompressionLevel.Fastest);
        zip.CreateEntry("assets/");
        if (Directory.Exists(paths.DataAssetsRoot))
        {
            AddDirectoryToZip(zip, paths.DataAssetsRoot, "assets");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"yunxi-bank-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            ExtractZipSafe(uploadedFile, tempDir);
            var sourceDb = FindQuestionDb(tempDir) ?? throw new InvalidOperationException("压缩包中没有找到 question-bank.db / .sqlite 题库文件");
            var sourceAssets = FindAssetsDirectory(tempDir, sourceDb) ?? throw new InvalidOperationException("压缩包中没有找到 assets 目录");

            ValidateQuestionDb(sourceDb);
            var dataRoot = Directory.GetParent(paths.SqlitePath)?.FullName ?? paths.BaseRoot;
            Directory.CreateDirectory(dataRoot);
            var backupDir = Path.Combine(dataRoot, "_backups", "bank-" + Timestamp());
            Directory.CreateDirectory(backupDir);
            if (File.Exists(paths.SqlitePath)) File.Copy(paths.SqlitePath, Path.Combine(backupDir, "question-bank.db"), true);
            if (Directory.Exists(paths.DataAssetsRoot))
            {
                CopyDirectory(paths.DataAssetsRoot, Path.Combine(backupDir, "assets"));
            }

            File.Copy(sourceDb, paths.SqlitePath, true);
            ReplaceDirectory(paths.DataAssetsRoot, sourceAssets);
            return new
            {
                ok = true,
                type = "bank",
                message = "题库和图片资源已上传",
                backup = backupDir,
                status = GetStatus(paths)
            };
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    public static object ImportUserData(AppPaths paths, AuthStore auth, string uploadedFile, string originalName)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (ext != ".zip") throw new InvalidOperationException("用户数据上传仅支持 zip 包");
        var tempDir = Path.Combine(Path.GetTempPath(), $"yunxi-userdata-{Guid.NewGuid():N}");
        var importDir = Path.Combine(tempDir, "import");
        try
        {
            Directory.CreateDirectory(importDir);
            ExtractZipSafe(uploadedFile, importDir);

            var files = CollectUserDataFiles(importDir).ToList();
            if (files.Count == 0) throw new InvalidOperationException("没有找到可导入的用户数据文件");
            foreach (var file in files) ValidateJsonFile(file.Path, file.Name);

            Directory.CreateDirectory(paths.UserDataRoot);
            var backupRoot = Path.Combine(paths.UserDataRoot, "_backups");
            Directory.CreateDirectory(backupRoot);
            var backupZip = Path.Combine(backupRoot, "userdata-" + Timestamp() + ".zip");
            using (var zip = ZipFile.Open(backupZip, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(paths.UserDataRoot, "*", SearchOption.AllDirectories)
                             .Where(path => !IsUnderBackup(path, backupRoot)))
                {
                    var relative = Path.GetRelativePath(paths.UserDataRoot, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }
            }

            foreach (var old in Directory.EnumerateFiles(paths.UserDataRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                File.Delete(old);
            }
            if (File.Exists(paths.AuthDataPath)) File.Delete(paths.AuthDataPath);

            foreach (var file in files)
            {
                var target = Path.Combine(paths.UserDataRoot, file.Name);
                File.Copy(file.Path, target, true);
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

    private static IEnumerable<(string Path, string Name)> CollectUserDataFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("accounts.dat", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var cleanName = name.Equals("accounts.dat", StringComparison.OrdinalIgnoreCase)
                    ? "accounts.dat"
                    : CleanUserName(Path.GetFileNameWithoutExtension(name)) + ".json";
                yield return (file, cleanName);
            }
        }
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

    private static void ValidateQuestionDb(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        foreach (var table in new[] { "course", "coursechapter", "coursesubject", "coursesubjecttype" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select count(*) from " + table;
            cmd.ExecuteScalar();
        }
    }

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
        var fullDestination = Path.GetFullPath(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
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

    private static void ReplaceDirectory(string target, string source)
    {
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        CopyDirectory(source, target);
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

sealed class AppPaths
{
    public AppPaths(IConfiguration configuration, IWebHostEnvironment env)
    {
        BaseRoot = env.ContentRootPath;
        PublicRoot = ResolvePublicRoot(configuration["App:PublicRoot"]);
        SqlitePath = Resolve(configuration["App:SqlitePath"] ?? "../data/question-bank.db");
        DataAssetsRoot = Resolve(configuration["App:DataAssetsRoot"] ?? "../data/assets");
        UserDataRoot = Resolve(configuration["App:UserDataRoot"] ?? "../userdata");
        DefaultLoginPassword = configuration["App:DefaultLoginPassword"] ?? "123456";
    }

    public string BaseRoot { get; }
    public string PublicRoot { get; }
    public string SqlitePath { get; }
    public string DataAssetsRoot { get; }
    public string UserDataRoot { get; }
    public string AuthDataPath => Path.Combine(UserDataRoot, "accounts.dat");
    public string DefaultLoginPassword { get; }

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
        var bundled = Resolve("public");
        if (Directory.Exists(bundled)) return bundled;
        var sibling = Path.GetFullPath(Path.Combine(BaseRoot, "..", "..", "webapp", "public"));
        return Directory.Exists(sibling) ? sibling : bundled;
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
            ["hash"] = PasswordHash(password, salt),
            ["changedAt"] = NowText()
        };
    }

    public bool Verify(string password, Dictionary<string, object?> profile)
    {
        var salt = GetProfileString(profile, "salt");
        var hash = GetProfileString(profile, "hash");
        return salt.Length > 0 && hash.Length > 0 && string.Equals(PasswordHash(password, salt), hash, StringComparison.OrdinalIgnoreCase);
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_paths.UserDataRoot);
            File.WriteAllText(_paths.AuthDataPath, JsonSerializer.Serialize(_auth), Encoding.UTF8);
        }
    }

    public void Reload()
    {
        lock (_lock)
        {
            _auth = Load();
        }
    }

    private Dictionary<string, Dictionary<string, object?>> Load()
    {
        Directory.CreateDirectory(_paths.UserDataRoot);
        if (!File.Exists(_paths.AuthDataPath)) return new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object?>>>(File.ReadAllText(_paths.AuthDataPath, Encoding.UTF8));
            return raw is null
                ? new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Dictionary<string, object?>>(raw, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string GetProfileString(Dictionary<string, object?> profile, string key)
    {
        return profile.TryGetValue(key, out var value) ? ScalarString(value) : "";
    }

    private static string PasswordHash(string password, string salt)
    {
        var bytes = Encoding.UTF8.GetBytes(salt + "\n" + password);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
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
        if (File.Exists(sqlitePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath) ?? ".");
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath }.ToString());
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
                bstopflag integer default 0
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

    public IEnumerable<object> GetCourses(string search, bool availableOnly)
    {
        var rows = Query(@"
            select c.icourseid, c.ccoursename, c.ihadbuy, c.dchangedate, c.dchapterchange, c.dsubjectchange,
                   cl.ccoursecname, sc.csubclassname,
                   (select count(*) from coursesubject s where s.icourseid = c.icourseid and coalesce(s.bstopflag, 0)=0) as subject_count
            from course c
            left join courseclass cl on c.iclassid=cl.iclassid
            left join coursesubclass sc on c.isubclassid=sc.isubclassid
            where coalesce(c.bstopflag, 0)=0
              and (@available = 0 or ((select count(*) from coursesubject s where s.icourseid = c.icourseid and coalesce(s.bstopflag, 0)=0) > 0))
              and (@search = '' or c.ccoursename like @like or cl.ccoursecname like @like or sc.csubclassname like @like)
            order by c.ihadbuy desc, cl.iindex, sc.iindex, c.iindex",
            new Dictionary<string, object?> { ["@available"] = availableOnly ? 1 : 0, ["@search"] = search, ["@like"] = "%" + search + "%" });
        return rows.Select(r => new
        {
            id = ToInt(r["icourseid"]),
            name = ToStr(r["ccoursename"]),
            category = ToStr(r["ccoursecname"]),
            subcategory = ToStr(r["csubclassname"]),
            owned = ToInt(r["ihadbuy"]) > 0,
            questionCount = ToInt(r["subject_count"]),
            changedAt = MaxDateString(r["dchangedate"], r["dchapterchange"], r["dsubjectchange"])
        }).ToList();
    }

    public static object TryReadTableCounts(string sqlitePath)
    {
        if (!File.Exists(sqlitePath)) return new { ok = false };
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
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
            return new { ok = false, error = ex.Message };
        }
    }

    public IEnumerable<object> GetChapters(int courseId)
    {
        var rows = Query(@"
            select ch.ichapterid, ch.cchaptername, ch.cchaptercode, ch.igrade, ch.itype, ch.icount,
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
            select s.isubjectid, s.icourseid, s.ichapterid, s.isubjecttype, s.iindex,
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
            title = StripHtml(DecryptField(r["ctitle"], r["dupdatedate"])),
            answerCount = ToInt(r["ianswercount"])
        });
        if (search.Length > 0) items = items.Where(i => i.title.Contains(search, StringComparison.OrdinalIgnoreCase));
        return items.ToList();
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
        var options = ExtractOptions(title, ToInt(r["ianswercount"]));
        var stem = options.Count > 0 ? RemoveOptions(title) : title;
        if (options.Count == 0 && question.Length > 0)
        {
            options = ExtractOptions(question, ToInt(r["ianswercount"]));
            if (options.Count > 0) question = "";
        }
        return new
        {
            id = ToInt(r["isubjectid"]),
            courseId = ToInt(r["icourseid"]),
            chapterId = ToInt(r["ichapterid"]),
            chapterName = ToStr(r["cchaptername"]),
            type = ToStr(r["csubjectname"]),
            subjectType = ToInt(r["isubjecttype"]),
            score = ToStr(r["iscore"]),
            stem,
            extraQuestion = question,
            answer,
            description,
            options,
            updatedAt = ToDateString(r["dupdatedate"])
        };
    }

    private List<Dictionary<string, object?>> Query(string sql, Dictionary<string, object?> parameters)
    {
        if (!File.Exists(_paths.SqlitePath)) throw new FileNotFoundException("SQLite question bank not found. Upload or mount data/question-bank.db first.", _paths.SqlitePath);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.SqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var pair in parameters) cmd.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
        conn.Open();
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

    private static List<object> ExtractOptions(string html, int answerCount)
    {
        var text = StripHtml(html).Replace("\r", "\n");
        var regex = new Regex(@"(?m)(?:^|\n)\s*([A-H])[\.\u3001\uff0e:：]\s*");
        var matches = regex.Matches(text);
        var options = new List<object>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var label = matches[i].Groups[1].Value;
            var optionText = text.Substring(start, Math.Max(0, end - start)).Trim();
            if (optionText.Length > 0) options.Add(new { label, text = optionText });
        }
        if (answerCount > 0 && options.Count > answerCount && answerCount <= 8) options = options.Take(answerCount).ToList();
        return options;
    }

    private static string RemoveOptions(string html)
    {
        var text = StripHtml(html).Replace("\r", "\n");
        var match = Regex.Match(text, @"(?m)(?:^|\n)\s*[A-H][\.\u3001\uff0e:：]\s*");
        if (!match.Success) return html;
        return WebUtility.HtmlEncode(text[..match.Index].Trim()).Replace("\n", "<br>");
    }

    private static string NormalizeHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = html.Replace("\\", "/");
        html = Regex.Replace(html, @"(?i)(src=[""']?)(?:file:///android_asset/)?(?:\.\./)?(?:data/)?", "$1/assets/");
        return html.Replace("\r\n", "\n").Replace("\n", "<br>");
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

