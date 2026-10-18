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

internal static class PushEndpoints
{
    public static void MapPush(this WebApplication app)
    {
    app.MapGet("/api/push/config", (HttpContext context, PushConfigStore store, string? user) =>
    {
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        return Results.Json(store.ReadForApi(clean));
    });

    app.MapPut("/api/push/config", async (HttpContext context, PushConfigStore store, string? user) =>
    {
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        var body = await new StreamReader(context.Request.Body, Encoding.UTF8).ReadToEndAsync();
        PushConfig incoming;
        try { incoming = PushConfigStore.Parse(body); }
        catch (JsonException ex) { return Results.Json(new { ok = false, error = "推送配置不是有效 JSON：" + ex.Message }, statusCode: 400); }
        try
        {
            return Results.Json(store.Save(clean, incoming));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
        }
    });

    /* 「发送测试」：用当前配置立刻发一条，**不写** lastSentDate。
       开关关着也能测 —— 用户改完地址第一件事就是想验证通不通，不该先逼他打开总开关。 */

    app.MapPost("/api/push/test", async (HttpContext context, PushConfigStore store, UserDataStore userData, WebPushSender webPush, string? user) =>
    {
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        var cfg = store.Read(clean);
        /* webpush 没有「地址」这个概念（凭据是每台设备的订阅），所以这条前置检查要跳过，
           否则浏览器通知永远点不动「发送测试」。 */
        if (cfg.Channel.Type != "webpush" && string.IsNullOrWhiteSpace(cfg.Channel.Url))
        {
            return Results.Json(new { ok = false, error = "还没有填写推送地址" }, statusCode: 400);
        }
        var now = DateTime.Now;
        var digest = await PushDigestReader.ComputeAsync(userData, clean, now) ?? new PushDigest { DaysLeft = -1 };
        /* 今天已经练过 + style=remind 时 Build 会返回 null（「今天不发」），但用户点的是
           「发送测试」—— 必须发出去一条，否则他会以为配置坏了。给一条明确的测试消息。 */
        var message = PushMessageBuilder.Build(cfg, digest, now, test: true)
            ?? new PushMessage("【测试】提醒已配置好",
                digest.DoneToday > 0 ? $"今天已练 {digest.DoneToday} 题，之后不会再打扰你" : "正式提醒会在每天 " + cfg.Time + " 发出",
                PushMessageBuilder.UrlFor(cfg.BaseUrl));
        var (ok, error) = await PushDelivery.SendAsync(webPush, clean, cfg.Channel, message, CancellationToken.None);
        return Results.Json(new
        {
            ok,
            error,
            message = new { title = message.Title, body = message.Body, url = message.Url }
        });
    });

    /* ── 浏览器通知（Web Push）的绑定 ────────────────────────────────────────
       为什么单独三个端点而不是并进 PUT /api/push/config：订阅是**每台设备一份凭据**，
       走整包保存必然出现「手机保存一次就把电脑那份冲掉」。 */

    app.MapGet("/api/push/vapid", (HttpContext context, WebPushSender webPush) =>
    {
        /* 公钥本来就是公开的（它要发到每台设备上），但仍然要求已登录：
           没登录的人不需要知道这个站开了推送。ResolveRequestedUser 只用来验会话。 */
        try { ResolveRequestedUser(context, null); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        return Results.Json(new { ok = true, publicKey = webPush.PublicKey });
    });

    app.MapPost("/api/push/subscribe", async (HttpContext context, PushConfigStore store, string? user) =>
    {
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        var body = await new StreamReader(context.Request.Body, Encoding.UTF8).ReadToEndAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            /* 前端直接 POST PushSubscription.toJSON()，所以同时接受
               { endpoint, keys:{p256dh,auth} } 和包一层 { subscription: {...} } 两种形状。 */
            var source = root.ValueKind == JsonValueKind.Object
                         && root.TryGetProperty("subscription", out var nested)
                         && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;
            var subscription = new PushSubscription
            {
                Endpoint = ReadString(source, "endpoint"),
                UserAgent = (context.Request.Headers.UserAgent.ToString() ?? "").Trim(),
            };
            if (source.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
            {
                subscription.P256dh = ReadString(keys, "p256dh");
                subscription.Auth = ReadString(keys, "auth");
            }
            return Results.Json(store.AddSubscription(clean, subscription));
        }
        catch (JsonException ex)
        {
            return Results.Json(new { ok = false, error = "订阅内容不是有效 JSON：" + ex.Message }, statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
        }
    });

    app.MapPost("/api/push/unsubscribe", async (HttpContext context, PushConfigStore store, string? user) =>
    {
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        var body = await new StreamReader(context.Request.Body, Encoding.UTF8).ReadToEndAsync();
        string? id = null;
        string? endpoint = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                id = doc.RootElement.TryGetProperty("id", out var idValue) ? idValue.ToString() : null;
                endpoint = doc.RootElement.TryGetProperty("endpoint", out var epValue) ? epValue.ToString() : null;
            }
        }
        catch (JsonException ex)
        {
            return Results.Json(new { ok = false, error = "请求内容不是有效 JSON：" + ex.Message }, statusCode: 400);
        }
        var (config, removed) = store.RemoveSubscription(clean, id, endpoint);
        return Results.Json(new { ok = true, removed, config });
    });

    /* 「服务端看到什么」：把服务端算出的到期数/已练数/连续天数摊开给前端。
       这个端点是**双份实现的探针** —— REVIEW_INTERVAL_DAYS 在 app.js:180 和
       PushDigestReader 各有一份，一旦漂移，这里和错题本的数字会当场对不上。 */

    app.MapGet("/api/push/preview", async (HttpContext context, PushConfigStore store, UserDataStore userData, string? user, string? style, string? baseUrl) =>
    {
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        var now = DateTime.Now;
        var cfg = store.Read(clean);
        /* style / baseUrl 允许用查询参数覆盖：设置页里用户改了「内容风格」但还没保存时，
           预览要跟着变，否则他得先保存才看得到效果。只影响这一次预览，不落盘。 */
        if (!string.IsNullOrWhiteSpace(style)) cfg.Style = style.Trim().ToLowerInvariant();
        /* 空串 = 用户把跳转地址清空了，那按「用服务端默认地址」预览 —— 与真正发送时一致
           （Save 和 Read 都会把空值补成默认值），否则预览里没链接、实际发出去却有。 */
        if (baseUrl is not null)
        {
            var overridden = baseUrl.Trim().TrimEnd('/');
            cfg.BaseUrl = overridden.Length == 0 ? store.DefaultBaseUrl : overridden;
        }
        var digest = await PushDigestReader.ComputeAsync(userData, clean, now);
        if (digest is null)
        {
            return Results.Json(new { ok = false, error = "读取学习数据失败，暂时无法计算今日摘要" }, statusCode: 500);
        }
        var message = PushMessageBuilder.Build(cfg, digest, now);
        return Results.Json(new
        {
            ok = true,
            digest = new
            {
                due = digest.Due,
                wrongTotal = digest.WrongTotal,
                doneToday = digest.DoneToday,
                correctToday = digest.CorrectToday,
                verifiedToday = digest.VerifiedToday,
                streak = digest.Streak,
                daysLeft = digest.DaysLeft,
                dailyTarget = digest.DailyTarget,
                courseName = digest.CourseName,
                daysSinceLast = digest.DaysSinceLast
            },
            willSend = message is not null,
            message = message is null ? null : new { title = message.Title, body = message.Body, url = message.Url }
        });
    });

    /* 这里原有 POST /api/push/run（管理员强制跑一轮）。删除理由：/api/push/test 已经能
       验证「能不能发出去」，而「为什么今天没发」看 docker logs 里调度器那几行更直接。
       留着它多一个能群发全站提醒的接口，收益不抵风险。 */
    }
}
