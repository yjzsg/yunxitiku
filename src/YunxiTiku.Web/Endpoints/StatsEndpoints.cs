using System.Text.Json;
using static AppHelpers;

/// <summary>
/// 全站答题统计接口。三件事：上报作答、按题查比例、按课查总览。
///
/// 上报的语义是「尽力而为」：客户端答完题先在本地排队，空闲时批量发出，失败就留着下次再发。
/// 所以这里不做任何幂等/去重（重复上报也只是多记一次作答），也不回具体数字——
/// 回数字等于把「这一批到底记上了多少」变成客户端的强依赖，没必要。
/// </summary>
internal static class StatsEndpoints
{
    /// <summary>一次上报最多多少题。够客户端攒一两分钟的作答，又不至于让请求体失控。</summary>
    private const int MaxBatch = 500;

    public static void MapStats(this WebApplication app)
    {
        app.MapPost("/api/stats/answers", async (HttpContext context, QuestionStatsStore stats, QuestionBank bank) =>
        {
            if (string.IsNullOrEmpty(SessionUser(context)))
            {
                return Results.Json(new { ok = false, error = "请先登录" }, statusCode: 401);
            }
            var (body, error) = await ReadBodyWithLimitAsync(context.Request.Body, 64 * 1024, context.RequestAborted);
            if (error is not null)
            {
                return Results.Json(new { ok = false, error }, statusCode: 400);
            }
            List<(int QuestionId, bool Correct)> items;
            try
            {
                items = ParseItems(body ?? "");
            }
            catch (JsonException)
            {
                return Results.Json(new { ok = false, error = "请求体不是合法的 JSON" }, statusCode: 400);
            }
            if (items.Count == 0)
            {
                return Results.Json(new { ok = true, accepted = 0 });
            }
            try
            {
                var accepted = stats.Record(items, bank);
                return Results.Json(new { ok = true, accepted });
            }
            catch (Exception ex)
            {
                /* 统计是旁路功能：题库还没上传（FileNotFoundException）之类的失败不该让客户端
                   以为「答题记录坏了」，回 503 让客户端继续排队、下次再试。 */
                return Results.Json(new { ok = false, error = SafeErrorText(ex) }, statusCode: 503);
            }
        });

        app.MapGet("/api/stats/questions", (HttpContext context, QuestionStatsStore stats, string? ids) =>
        {
            if (string.IsNullOrEmpty(SessionUser(context)))
            {
                return Results.Json(new { ok = false, error = "请先登录" }, statusCode: 401);
            }
            var list = ParseIdList(ids).Take(200).ToList();
            if (list.Count == 0)
            {
                return Results.Json(new { ok = true, threshold = QuestionStatsStore.QuestionMinAttempts, items = Array.Empty<object>() });
            }
            try
            {
                var found = stats.GetQuestions(list);
                var items = found
                    .OrderByDescending(pair => pair.Value.Wrong / (double)Math.Max(1, pair.Value.Attempts))
                    .Select(pair => new
                    {
                        id = pair.Key,
                        attempts = pair.Value.Attempts,
                        wrong = pair.Value.Wrong,
                        rate = Math.Round(pair.Value.Wrong / (double)Math.Max(1, pair.Value.Attempts), 4),
                    })
                    .ToList();
                return Results.Json(new { ok = true, threshold = QuestionStatsStore.QuestionMinAttempts, items });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = SafeErrorText(ex) }, statusCode: 503);
            }
        });

        app.MapGet("/api/stats/course", (HttpContext context, QuestionStatsStore stats, int courseId) =>
        {
            if (string.IsNullOrEmpty(SessionUser(context)))
            {
                return Results.Json(new { ok = false, error = "请先登录" }, statusCode: 401);
            }
            if (courseId <= 0)
            {
                return Results.Json(new { ok = false, error = "courseId 无效" }, statusCode: 400);
            }
            try
            {
                var (attempts, wrong) = stats.GetCourse(courseId);
                return Results.Json(new
                {
                    ok = true,
                    courseId,
                    attempts,
                    wrong,
                    rate = attempts == 0 ? 0 : Math.Round(wrong / (double)attempts, 4),
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = SafeErrorText(ex) }, statusCode: 503);
            }
        });
    }

    /// <summary>
    /// 解析 <c>{"items":[{"q":101,"ok":true}]}</c>。宽容一点：<c>ok</c> 接受 true/false、1/0、"1"/"0"，
    /// 因为老客户端可能把布尔压成数字。解析不了的条目直接丢，不让一条脏数据废掉整批。
    /// </summary>
    private static List<(int QuestionId, bool Correct)> ParseItems(string body)
    {
        var result = new List<(int, bool)>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("items", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var element in array.EnumerateArray())
        {
            if (result.Count >= MaxBatch) break;
            if (element.ValueKind != JsonValueKind.Object) continue;
            var id = 0;
            if (element.TryGetProperty("q", out var q) && q.ValueKind == JsonValueKind.Number) id = q.GetInt32();
            if (id <= 0) continue;
            if (!element.TryGetProperty("ok", out var ok)) continue;
            var correct = ok.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => ok.GetInt32() != 0,
                JsonValueKind.String => ok.GetString() is "1" or "true" or "True",
                _ => false,
            };
            result.Add((id, correct));
        }
        return result;
    }
}
