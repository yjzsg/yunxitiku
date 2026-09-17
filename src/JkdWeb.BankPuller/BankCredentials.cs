namespace JkdWeb.BankPuller;

/// <summary>
/// 上游题库服务（金考典 ExamClientService）的账号来源。
///
/// **不再把账号写在代码里**：这个工程会被内嵌进公开仓库（yunxitiku-docker-public），
/// 一旦内置就等于公开。所以账号只从两个地方来，按优先级：
///
/// 1. 显式传入（网站是 <c>App:BankServiceUser</c> / <c>App:BankServicePassword</c> 配置键，
///    CLI 是 <c>--user</c> / <c>--password</c>）；
/// 2. 环境变量 <c>JKD_BANK_USER</c> / <c>JKD_BANK_PASSWORD</c>（Docker 用 <c>--env-file</c>，
///    Windows 侧由 start-windows.ps1 从 data/bank-credentials.env 载入）。
///
/// 服务地址不是机密，保留内置默认值（金考典官方地址，客户端本来就用它），但仍可覆盖。
/// </summary>
public static class BankCredentials
{
    public const string UserVariable = "JKD_BANK_USER";
    public const string PasswordVariable = "JKD_BANK_PASSWORD";
    public const string UrlVariable = "JKD_BANK_URL";

    public const string MissingHint =
        "请任选一种方式配置：" +
        "① 网站：在 appsettings.json 或 docker-compose.override.yml 里设置 " +
        "App__BankServiceUser / App__BankServicePassword；" +
        "② 命令行：设置环境变量 JKD_BANK_USER / JKD_BANK_PASSWORD，或用 --user / --password 传入。";

    /// <summary>读环境变量（空值一律当未设置）。</summary>
    public static string FromEnvironment(string variable)
        => (Environment.GetEnvironmentVariable(variable) ?? string.Empty).Trim();

    /// <summary>服务地址：显式 &gt; 环境变量 &gt; 内置默认。</summary>
    public static string ResolveUrl(string? explicitUrl)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl)) return explicitUrl.Trim();
        var fromEnv = FromEnvironment(UrlVariable);
        return fromEnv.Length > 0 ? fromEnv : SoapClient.DefaultServiceUrl;
    }

    public static string ResolveUser(string? explicitUser)
    {
        if (!string.IsNullOrWhiteSpace(explicitUser)) return explicitUser.Trim();
        return FromEnvironment(UserVariable);
    }

    public static string ResolvePassword(string? explicitPassword)
    {
        if (!string.IsNullOrWhiteSpace(explicitPassword)) return explicitPassword.Trim();
        return FromEnvironment(PasswordVariable);
    }

    /// <summary>拉取前调用：账号缺失就抛出带指引的错误，而不是拿空账号去请求。</summary>
    public static void EnsureConfigured(string user, string password)
    {
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password)) return;
        var missing = string.IsNullOrWhiteSpace(user) ? "账号" : "密码";
        throw new InvalidOperationException($"缺少上游题库服务{missing}。{MissingHint}");
    }

    /// <summary>给日志/界面用的脱敏描述（永远不要打印完整口令）。</summary>
    public static string Describe(string url, string user, string password)
        => $"{url} (账号 {user}，口令 {(password.Length > 0 ? "已配置" : "未配置")})";
}
