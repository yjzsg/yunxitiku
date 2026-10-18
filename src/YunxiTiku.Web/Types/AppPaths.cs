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
