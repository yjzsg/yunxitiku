using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// AI 解析的配置（管理员可改，改完立刻生效，不用重启）。
///
/// 刻意把配置放在**数据目录**（与题库同级的 <c>ai-config.json</c>）而不是代码或环境变量：
///   · 密钥不该进仓库；容器里改环境变量要重建，而这里只要管理员在页面上填一次；
///   · 配置文件与统计库同目录，备份时一起走；
///   · 读文件只在请求进来时发生（几百字节），不做缓存，避免「改了没生效」的糊涂账。
/// </summary>
sealed class AiSettingsStore
{
    public sealed class Settings
    {
        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string Model { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int DailyLimit { get; set; } = 20;
        public int MaxTokens { get; set; } = 900;
        public string Prompt { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly AppPaths _paths;
    private readonly object _writeLock = new();

    public AiSettingsStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string ConfigPath
    {
        get
        {
            var full = Path.GetFullPath(_paths.SqlitePath);
            var dir = Path.GetDirectoryName(full) ?? _paths.BaseRoot;
            return Path.Combine(dir, "ai-config.json");
        }
    }

    /// <summary>读配置；文件不存在或损坏时返回「关闭」——宁可不出按钮，也不要拿坏配置去请求上游。</summary>
    public Settings Read()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new Settings();
            var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
            // 手写文件时可能漏字段或写成 0，这里兜一下范围。
            settings.DailyLimit = Math.Clamp(settings.DailyLimit, 0, 1000);
            settings.MaxTokens = Math.Clamp(settings.MaxTokens, 128, 4096);
            settings.BaseUrl = (settings.BaseUrl ?? "").Trim();
            settings.Model = (settings.Model ?? "").Trim();
            return settings;
        }
        catch
        {
            return new Settings();
        }
    }

    /// <summary>写配置：先写临时文件再整体替换，避免半截 JSON 被读成「关闭」。</summary>
    public void Write(Settings settings)
    {
        lock (_writeLock)
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, ConfigPath, true);
            RestrictFilePermissions(ConfigPath);
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // best-effort：某些文件系统（如部分挂载卷）不支持 chmod。
        }
    }
}
