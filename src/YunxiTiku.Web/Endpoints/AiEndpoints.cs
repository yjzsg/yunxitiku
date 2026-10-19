using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static AppHelpers;

/// <summary>
/// AI 解析：按题生成讲解，同题同模型同提示词只向上游要一次（见 <see cref="AiExplainStore"/>）。
///
/// 为什么放在服务端而不是浏览器直连：密钥不能下发到客户端；配额要按账号算；
/// 缓存要跨设备共享（A 生成的解析，B 点开就是秒回，不再付费）。
///
/// 未开启时接口回 503 + code=ai_disabled，前端据此不显示入口 ——
/// 「按钮点了才报错」比「根本没有按钮」更让人困惑。
/// </summary>
internal static class AiEndpoints
{
    private const string DefaultPrompt =
        "你是中国职业资格考试的辅导老师。用简体中文讲解这道题：" +
        "① 这道题考的是哪个知识点；② 为什么正确答案对；③ 其余选项各自错在哪；" +
        "④ 给一句能记住的提示。不超过 400 字，用纯文本，不要标题、不要 Markdown 记号。";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>按「账号 + 当天」记调用次数。进程内即可：重启丢计数最多让人多用一次，不值得为它落库。</summary>
    private static readonly Dictionary<string, (string Day, int Count)> Quota = new();
    private static readonly object QuotaLock = new();

    public static void MapAi(this WebApplication app)
    {
        app.MapGet("/api/ai/status", (HttpContext context, AiSettingsStore store) =>
        {
            var user = SessionUser(context);
            if (string.IsNullOrEmpty(user)) return Results.Json(new { ok = false, error = "请先登录" }, statusCode: 401);
            var settings = store.Read();
            var enabled = settings.Enabled && HasKey(settings);
            return Results.Json(new { ok = true, enabled, model = enabled ? settings.Model : "" });
        });

        app.MapPost("/api/ai/explain", async (HttpRequest request, HttpContext context,
            QuestionBank bank, AuthStore auth, AiSettingsStore store, AiExplainStore cache) =>
        {
            var user = SessionUser(context);
            if (string.IsNullOrEmpty(user)) return Results.Json(new { ok = false, error = "请先登录" }, statusCode: 401);

            var settings = store.Read();
            if (!settings.Enabled || !HasKey(settings))
            {
                return Results.Json(new { ok = false, code = "ai_disabled", error = "AI 解析未开启，请联系管理员" }, statusCode: 503);
            }

            var (body, bodyError) = await ReadBodyWithLimitAsync(request.Body, 4 * 1024, context.RequestAborted);
            if (bodyError is not null) return Results.Json(new { ok = false, error = bodyError }, statusCode: 400);
            int questionId;
            bool force;
            try
            {
                var node = JsonNode.Parse(body ?? "null") as JsonObject;
                questionId = node?["questionId"]?.GetValue<int>() ?? 0;
                force = node?["force"]?.GetValue<bool>() ?? false;
            }
            catch
            {
                return Results.Json(new { ok = false, error = "请求体不是合法 JSON" }, statusCode: 400);
            }
            if (questionId <= 0) return Results.Json(new { ok = false, error = "缺少 questionId" }, statusCode: 400);

            var courseId = bank.CourseIdOfSubject(questionId);
            if (courseId <= 0) return Results.Json(new { ok = false, error = "题目不存在" }, statusCode: 404);
            if (!UserCourses.IsAllowed(auth, user, courseId))
            {
                return Results.Json(new { ok = false, error = "未分配该题库" }, statusCode: 403);
            }

            var key = CacheKey(settings, questionId);
            if (!force)
            {
                var hit = cache.Get(key);
                if (!string.IsNullOrEmpty(hit))
                {
                    return Results.Json(new { ok = true, text = hit, cached = true, model = settings.Model });
                }
            }

            if (!TryTakeQuota(user, settings.DailyLimit, out var used))
            {
                return Results.Json(new
                {
                    ok = false,
                    code = "ai_quota",
                    error = $"今天的 AI 解析次数用完了（上限 {settings.DailyLimit} 次），明天再试",
                    used,
                }, statusCode: 429);
            }

            string text;
            try
            {
                text = await AskUpstream(settings, bank, questionId, context.RequestAborted);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AI explain failed: {ex.Message}");
                return Results.Json(new { ok = false, code = "ai_upstream", error = "AI 服务暂时不可用，请稍后再试" }, statusCode: 502);
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return Results.Json(new { ok = false, code = "ai_upstream", error = "AI 没有返回内容" }, statusCode: 502);
            }

            cache.Put(key, questionId, settings.Model, text);
            return Results.Json(new { ok = true, text, cached = false, model = settings.Model });
        });

        app.MapGet("/api/admin/ai-config", (HttpContext context, AiSettingsStore store, AiExplainStore cache) =>
        {
            if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
            var settings = store.Read();
            var (entries, questions) = cache.Stats();
            return Results.Json(new
            {
                ok = true,
                enabled = settings.Enabled,
                baseUrl = settings.BaseUrl,
                model = settings.Model,
                hasKey = HasKey(settings),
                keyPreview = PreviewKey(settings.ApiKey),
                dailyLimit = settings.DailyLimit,
                maxTokens = settings.MaxTokens,
                prompt = settings.Prompt,
                cacheEntries = entries,
                cacheQuestions = questions,
            });
        });

        app.MapPost("/api/admin/ai-config", async (HttpRequest request, HttpContext context, AiSettingsStore store) =>
        {
            if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
            var (body, bodyError) = await ReadBodyWithLimitAsync(request.Body, 16 * 1024, context.RequestAborted);
            if (bodyError is not null) return Results.Json(new { ok = false, error = bodyError }, statusCode: 400);
            JsonObject? node;
            try
            {
                node = JsonNode.Parse(body ?? "null") as JsonObject;
            }
            catch
            {
                return Results.Json(new { ok = false, error = "请求体不是合法 JSON" }, statusCode: 400);
            }
            if (node is null) return Results.Json(new { ok = false, error = "请求体必须是 JSON 对象" }, statusCode: 400);

            var current = store.Read();
            var baseUrl = node["baseUrl"] is null ? current.BaseUrl : (node["baseUrl"]!.GetValue<string>() ?? "").Trim();
            if (baseUrl.Length > 0 && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")))
            {
                return Results.Json(new { ok = false, error = "BaseUrl 必须是 http(s) 开头的完整地址" }, statusCode: 400);
            }
            var model = node["model"] is null ? current.Model : (node["model"]!.GetValue<string>() ?? "").Trim();
            var enabled = node["enabled"]?.GetValue<bool>() ?? current.Enabled;
            var dailyLimit = node["dailyLimit"]?.GetValue<int>() ?? current.DailyLimit;
            var maxTokens = node["maxTokens"]?.GetValue<int>() ?? current.MaxTokens;
            var prompt = node["prompt"] is null ? current.Prompt : (node["prompt"]!.GetValue<string>() ?? "");
            var clearKey = node["clearKey"]?.GetValue<bool>() ?? false;
            // 没传 apiKey 或传空串 = 不改动现有密钥（管理页不会把密钥回显给前端）。
            var apiKey = node["apiKey"] is null ? current.ApiKey : (node["apiKey"]!.GetValue<string>() ?? "").Trim();
            if (string.IsNullOrEmpty(apiKey)) apiKey = current.ApiKey;
            if (clearKey) apiKey = "";

            if (enabled && (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model) || string.IsNullOrEmpty(baseUrl)))
            {
                return Results.Json(new { ok = false, error = "开启前请先填好 BaseUrl、模型和密钥" }, statusCode: 400);
            }

            var updated = new AiSettingsStore.Settings
            {
                Enabled = enabled,
                BaseUrl = baseUrl,
                Model = model,
                ApiKey = apiKey,
                DailyLimit = Math.Clamp(dailyLimit, 0, 1000),
                MaxTokens = Math.Clamp(maxTokens, 128, 4096),
                Prompt = prompt ?? "",
            };
            try
            {
                store.Write(updated);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = SafeErrorText(ex) }, statusCode: 500);
            }
            return Results.Json(new { ok = true, enabled = updated.Enabled, model = updated.Model, hasKey = HasKey(updated) });
        });
    }

    private static bool HasKey(AiSettingsStore.Settings settings)
        => !string.IsNullOrWhiteSpace(settings.ApiKey);

    private static string PreviewKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        return key.Length <= 4 ? "****" : "****" + key[^4..];
    }

    /// <summary>缓存键：模型 + 提示词（含默认提示词）+ 题号。改任何一项都会让旧缓存自然失效。</summary>
    private static string CacheKey(AiSettingsStore.Settings settings, int questionId)
    {
        var prompt = string.IsNullOrWhiteSpace(settings.Prompt) ? DefaultPrompt : settings.Prompt;
        var raw = $"{settings.Model}\n{prompt}\n{questionId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static bool TryTakeQuota(string user, int dailyLimit, out int used)
    {
        used = 0;
        if (dailyLimit <= 0) return true; // 0 = 不限制
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        lock (QuotaLock)
        {
            Quota.TryGetValue(user, out var record);
            var count = record.Day == day ? record.Count : 0;
            if (count >= dailyLimit)
            {
                used = count;
                return false;
            }
            used = count + 1;
            Quota[user] = (day, used);
            return true;
        }
    }

    private static async Task<string> AskUpstream(AiSettingsStore.Settings settings, QuestionBank bank, int questionId, CancellationToken token)
    {
        var question = bank.GetQuestion(questionId) ?? throw new InvalidOperationException("题目不存在");
        var node = JsonSerializer.SerializeToNode(question) as JsonObject;
        var stem = node?["stem"]?.GetValue<string>() ?? "";
        var extra = node?["extraQuestion"]?.GetValue<string>() ?? "";
        var answer = node?["answer"]?.GetValue<string>() ?? "";
        var description = node?["description"]?.GetValue<string>() ?? "";
        var options = new StringBuilder();
        if (node?["options"] is JsonArray list)
        {
            foreach (var item in list)
            {
                var label = item?["label"]?.GetValue<string>() ?? "";
                var text = item?["text"]?.GetValue<string>() ?? "";
                options.Append(label).Append(". ").Append(text).Append('\n');
            }
        }

        var userPrompt = new StringBuilder()
            .AppendLine("题干：").AppendLine(stem).AppendLine()
            .Append(extra.Length > 0 ? "补充材料：\n" + extra + "\n\n" : "")
            .Append(options.Length > 0 ? "选项：\n" + options + "\n" : "")
            .AppendLine("正确答案：" + answer)
            .AppendLine(description.Length > 0 ? "官方解析：" + description : "")
            .ToString();

        var payload = new
        {
            model = settings.Model,
            messages = new object[]
            {
                new { role = "system", content = string.IsNullOrWhiteSpace(settings.Prompt) ? DefaultPrompt : settings.Prompt },
                new { role = "user", content = userPrompt },
            },
            temperature = 0.3,
            max_tokens = settings.MaxTokens,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        req.Content = JsonContent.Create(payload);
        using var res = await Http.SendAsync(req, token);
        var raw = await res.Content.ReadAsStringAsync(token);
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"upstream {(int)res.StatusCode}: {Truncate(raw, 200)}");
        }
        using var doc = JsonDocument.Parse(raw);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return content.Trim();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
