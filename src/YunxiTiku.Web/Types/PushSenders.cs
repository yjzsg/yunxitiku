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

/// <summary>三个渠道适配器。都只是「拼一个 HTTP 请求」，没有 SDK 依赖。</summary>
static class PushSenders
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public static async Task<(bool Ok, string Error)> SendAsync(PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var type = (channel?.Type ?? "bark").Trim().ToLowerInvariant();
        var url = (channel?.Url ?? "").Trim();
        /* Server酱 / PushPlus：接口地址是服务商固定的，用户只填 key。地址留空就补默认值。 */
        if (url.Length == 0)
        {
            if (type == "serverchan") url = "https://sctapi.ftqq.com";
            else if (type == "pushplus") url = "https://www.pushplus.plus/send";
        }
        if (url.Length == 0) return (false, "推送地址为空");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (false, "推送地址必须是 http/https 开头的完整 URL");
        }
        try
        {
            return type switch
            {
                "ntfy" => await SendNtfyAsync(uri, channel!, message, ct),
                "webhook" => await SendWebhookAsync(uri, channel!, message, ct),
                "wechat" => await SendWeChatAsync(uri, message, ct),
                "serverchan" => await SendServerChanAsync(uri, channel!, message, ct),
                "pushplus" => await SendPushPlusAsync(uri, channel!, message, ct),
                _ => await SendBarkAsync(uri, message, ct),
            };
        }
        catch (TaskCanceledException)
        {
            return (false, "推送超时（10 秒无响应）");
        }
        catch (HttpRequestException ex)
        {
            return (false, "推送请求失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            return (false, "推送失败：" + ex.Message);
        }
    }

    /* Bark：POST 到用户给的地址（已含 device key），body 是 JSON。
       ⚠️ Bark 的 HTTP 状态码恒为 200，真正的结果在 body 的 code 字段里。 */
    private static async Task<(bool, string)> SendBarkAsync(Uri uri, PushMessage message, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = message.Title,
            ["body"] = message.Body,
            ["group"] = "云习题库",
            ["level"] = "active",
        };
        if (message.Url.Length > 0) payload["url"] = message.Url;
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(uri, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (false, $"Bark 返回 HTTP {(int)response.StatusCode}：{Short(text)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var number)
                && number != 200)
            {
                return (false, $"Bark 返回 code {number}：{Short(text)}");
            }
        }
        catch (JsonException)
        {
            // 自建 Bark / 中间层可能回纯文本，HTTP 2xx 就算成功
        }
        return (true, "");
    }

    /* ntfy：用 **JSON 发布**（POST 到用户填的 https://ntfy.sh/主题名）。
       ⚠️ 不能用 header 传标题：.NET 的 HttpClient **拒绝非 ASCII 的 header 值**，
       中文标题会直接抛「Request headers must contain only ASCII characters」——
       第一版就是拿 `Title` header 传的，实测 100% 失败（HTTP 请求都没发出去）。
       JSON body 没有这个限制。
       topic 不写进 JSON：URL 路径里已经有主题，ntfy 在 body 缺 topic 时用路径里那个。 */
    private static async Task<(bool, string)> SendNtfyAsync(Uri uri, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = message.Title,
            ["message"] = message.Body,
            ["priority"] = 3,
            ["tags"] = new[] { "books" },
        };
        if (message.Url.Length > 0) payload["click"] = message.Url;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(channel.Token)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + channel.Token.Trim());
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode
            ? (true, "")
            : (false, $"ntfy 返回 HTTP {(int)response.StatusCode}：{Short(text)}");
    }

    /* 企业微信群机器人：POST 到群机器人 webhook，body 形状固定。
       ⚠️ 和 Bark 一样，HTTP 恒 200，真正的结果在 body 的 errcode 里。
       注意它进的是**企业微信群**，不是个人微信 —— 要发到个人微信得用下面两个。 */
    private static async Task<(bool, string)> SendWeChatAsync(Uri uri, PushMessage message, CancellationToken ct)
    {
        var content = message.Url.Length > 0
            ? $"{message.Title}\n{message.Body}\n{message.Url}"
            : $"{message.Title}\n{message.Body}";
        var payload = new Dictionary<string, object?>
        {
            ["msgtype"] = "text",
            ["text"] = new Dictionary<string, object?> { ["content"] = content },
        };
        using var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(uri, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (false, $"企业微信返回 HTTP {(int)response.StatusCode}：{Short(text)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("errcode", out var code)
                && code.TryGetInt32(out var number)
                && number != 0)
            {
                return (false, $"企业微信返回 errcode {number}：{Short(text)}");
            }
        }
        catch (JsonException)
        {
            // 自建转发层可能回纯文本，HTTP 2xx 就算成功
        }
        return (true, "");
    }

    /* Server酱（个人微信）：POST 表单到 <base>/<SendKey>.send，code==0 才算成功。
       免费版每天 5 条 —— 一天一条够用。base 可换成 https://<uid>.push.ft07.com/send。 */
    private static async Task<(bool, string)> SendServerChanAsync(Uri uri, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var key = (channel.Token ?? "").Trim();
        if (key.Length == 0) return (false, "Server酱缺少 SendKey");
        var endpoint = uri.AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(key) + ".send";
        var form = new Dictionary<string, string>
        {
            ["title"] = message.Title,
            ["desp"] = message.Url.Length > 0 ? $"{message.Body}\n\n[点这里打开]({message.Url})" : message.Body,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (false, $"Server酱返回 HTTP {(int)response.StatusCode}：{Short(text)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var code)
                && code.TryGetInt32(out var number)
                && number != 0)
            {
                return (false, $"Server酱返回 code {number}：{Short(text)}");
            }
        }
        catch (JsonException) { }
        return (true, "");
    }

    /* PushPlus（个人微信）：POST JSON，code==200 才算成功。
       用 template=txt（纯文本），避免正文里的 < > & 需要再做一次 HTML 转义。 */
    private static async Task<(bool, string)> SendPushPlusAsync(Uri uri, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var token = (channel.Token ?? "").Trim();
        if (token.Length == 0) return (false, "PushPlus 缺少 token");
        var payload = new Dictionary<string, object?>
        {
            ["token"] = token,
            ["title"] = message.Title,
            ["content"] = message.Url.Length > 0 ? $"{message.Body}\n{message.Url}" : message.Body,
            ["template"] = "txt",
        };
        using var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(uri, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (false, $"PushPlus 返回 HTTP {(int)response.StatusCode}：{Short(text)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var code)
                && code.TryGetInt32(out var number)
                && number != 200)
            {
                return (false, $"PushPlus 返回 code {number}：{Short(text)}");
            }
        }
        catch (JsonException) { }
        return (true, "");
    }

    /* 通用 webhook：模板里的 {{title}} / {{body}} / {{url}} 会被**JSON 转义后**填入，
       所以模板本身可以直接写 JSON：
         {"msgtype":"text","text":{"content":"{{title}}\n{{body}}\n{{url}}"}}
       一条实现覆盖企业微信 / 飞书 / Server酱 / PushDeer / Telegram Bot。 */
    private static async Task<(bool, string)> SendWebhookAsync(Uri uri, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var template = (channel.Template ?? "").Trim();
        var body = template.Length == 0
            ? JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["title"] = message.Title,
                ["body"] = message.Body,
                ["url"] = message.Url,
            })
            : template
                .Replace("{{title}}", JsonEscape(message.Title), StringComparison.Ordinal)
                .Replace("{{body}}", JsonEscape(message.Body), StringComparison.Ordinal)
                .Replace("{{url}}", JsonEscape(message.Url), StringComparison.Ordinal);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(uri, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode
            ? (true, "")
            : (false, $"webhook 返回 HTTP {(int)response.StatusCode}：{Short(text)}");
    }

    private static string JsonEscape(string value)
    {
        var serialized = JsonSerializer.Serialize(value ?? "");
        return serialized.Length >= 2 ? serialized[1..^1] : "";
    }

    private static string Short(string text)
    {
        var value = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= 200 ? value : value[..200] + "…";
    }
}
