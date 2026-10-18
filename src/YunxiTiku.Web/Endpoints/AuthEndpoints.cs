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

internal static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {

        // 端点搬出 Program.cs 后拿不到顶层局部变量；AppPaths 是单例，按需取同一个实例。
        var paths = app.Services.GetRequiredService<AppPaths>();
    // 当前会话身份。登录页用它恢复「上次登录」，所以不带会话也能调；
    // 但它只回答"这张 cookie 是谁"，不泄露任何账号清单。
    app.MapGet("/api/auth/session", (HttpContext context, AuthStore auth, SessionStore sessionStore) =>
    {
        var user = sessionStore.Resolve(context.Request);
        if (user.Length == 0) return Results.Json(new { ok = false, user = "" });
        if (auth.IsDisabled(user))
        {
            sessionStore.SignOut(context);
            return Results.Json(new { ok = false, user = "", error = "账号已停用，请联系管理员" });
        }
        var profile = auth.Get(user);
        // 账号已被删除（admin 没有档案也合法）：会话作废
        if (profile is null && !user.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            sessionStore.SignOut(context);
            return Results.Json(new { ok = false, user = "", error = "账号不存在，请联系管理员" });
        }
        return Results.Json(new
        {
            ok = true,
            user,
            // 还是初始口令的账号：回登录页走改密流程，别让刷新绕过强制改密
            mustChangePassword = profile is null || auth.MustChangePassword(profile),
        });
    });

    app.MapPost("/api/auth/login", async (HttpContext context, AuthStore auth, SessionStore sessionStore) =>
    {
        var request = context.Request;
        var body = await ReadJsonBody(request);
        var user = CleanUserName(GetBodyString(body, "user"));
        var password = GetBodyString(body, "password");
        var profile = auth.Get(user);
        if (auth.IsDisabled(user)) return Results.Json(new { ok = false, error = "账号已停用，请联系管理员" }, statusCode: 403);
        if (profile is null)
        {
            if (!user.Equals("admin", StringComparison.OrdinalIgnoreCase))
            {
                // 不再区分"账号不存在(404)"与"密码不正确(401)"——那是用户名枚举预言机
                return Results.Json(new { ok = false, error = "账号或密码不正确" }, statusCode: 401);
            }
            if (password != paths.DefaultLoginPassword)
            {
                return Results.Json(new { ok = false, error = "账号或密码不正确" }, statusCode: 401);
            }
            var token = sessionStore.SignIn(context, user);
            return Results.Json(new { ok = true, user, mustChangePassword = true, token });
        }
        if (!auth.Verify(password, profile)) return Results.Json(new { ok = false, error = "账号或密码不正确" }, statusCode: 401);
        // 旧档案（单轮 SHA256）登录成功后自动升级为 PBKDF2 并回写
        if (auth.NeedsHashUpgrade(profile)) auth.UpgradeHash(user, password, profile);
        var loginToken = sessionStore.SignIn(context, user);
        return Results.Json(new { ok = true, user, mustChangePassword = auth.MustChangePassword(profile), token = loginToken });
    }).RequireRateLimiting("login");

    app.MapPost("/api/auth/change-password", async (HttpContext context, AuthStore auth, SessionStore sessionStore) =>
    {
        var request = context.Request;
        var body = await ReadJsonBody(request);
        var user = CleanUserName(Convert.ToString(context.Items[SessionStore.UserItemKey], CultureInfo.InvariantCulture) ?? "");
        var oldPassword = GetBodyString(body, "oldPassword");
        var newPassword = GetBodyString(body, "newPassword");
        if (newPassword.Length < 6) return Results.Json(new { ok = false, error = "新密码至少 6 位" }, statusCode: 400);
        var profile = auth.Get(user);
        var oldOk = profile is null ? oldPassword == paths.DefaultLoginPassword : auth.Verify(oldPassword, profile);
        if (!oldOk) return Results.Json(new { ok = false, error = "原密码不正确" }, statusCode: 401);
        var next = auth.CreateProfile(newPassword);
        next["mustChangePassword"] = false;
        /* 换凭据版本：这一步是"吊销的耐久性"来源。就算紧接着 sessions.json 写失败、
           旧会话记录还留在磁盘上，重启后解析时版本对不上 → 旧令牌一样进不来。 */
        AuthStore.RotateCredentialVersion(next);
        auth.Set(user, next);
        auth.Save();
        // 管理员口令已不再是初始口令，初始口令文件（若还在）就是过期信息。
        if (user.Equals("admin", StringComparison.OrdinalIgnoreCase)) paths.DeleteInitialAdminPasswordFile();
        // 改密即止损：先吊销该用户全部既有会话，再为本机重新签发一张
        var revokeOnChange = sessionStore.RevokeUser(user);
        var newToken = sessionStore.SignIn(context, user);
        return Results.Json(new { ok = true, user, token = newToken, sessionRevoke = SessionRevokeReport(revokeOnChange) });
    }).RequireRateLimiting("login");

    app.MapPost("/api/auth/logout", (HttpContext context, SessionStore sessionStore) =>
    {
        sessionStore.SignOut(context);
        return Results.Json(new { ok = true });
    });
    }
}
