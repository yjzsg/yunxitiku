using System.Globalization;
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
            List<(int QuestionId, bool Correct, string? Choice)> items;
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

        /* ── 题库质量闭环（管理员） ───────────────────────────────────────
           输入只有两样：全站匿名统计（题级次数 + 选项分布）与管理员会话。
           输出「值得看一眼」的题和原因。用户纠错数不在这里算 —— 它本来就在
           管理端的账号数据里，客户端合并比服务端再扫一遍所有用户 JSON 便宜。 */
        app.MapGet("/api/admin/quality", (HttpContext context, QuestionStatsStore stats, int? courseId, int? limit) =>
        {
            if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
            var course = Math.Max(0, courseId ?? 0);
            try
            {
                var rows = stats.GetQuality(course, limit ?? 60);
                var items = rows.Select(row =>
                {
                    var rate = row.Attempts > 0 ? row.Wrong / (double)row.Attempts : 0;
                    var ordered = row.Options.OrderByDescending(pair => pair.Value).ToList();
                    var total = ordered.Sum(pair => pair.Value);
                    var topShare = total > 0 ? ordered[0].Value / (double)total : 0;
                    return new
                    {
                        id = row.QuestionId,
                        courseId = row.CourseId,
                        chapterId = row.ChapterId,
                        attempts = row.Attempts,
                        wrong = row.Wrong,
                        rate = Math.Round(rate, 4),
                        options = row.Options.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value),
                        topOption = ordered.Count > 0 ? ordered[0].Key : "",
                        topShare = Math.Round(topShare, 4),
                        reasons = QualityReasons(row.Attempts, row.Wrong, row.Options),
                    };
                }).Where(item => item.reasons.Count > 0).ToList();
                return Results.Json(new
                {
                    ok = true,
                    courseId = course,
                    threshold = new
                    {
                        minAttempts = QuestionStatsStore.QualityMinAttempts,
                        flatMinAttempts = QuestionStatsStore.QualityFlatMinAttempts,
                        lowRate = QuestionStatsStore.QualityLowRate,
                        flatTopShare = QuestionStatsStore.QualityFlatTopShare,
                        flatGap = QuestionStatsStore.QualityFlatGap,
                        recheckAttempts = QuestionStatsStore.QualityRecheckAttempts,
                    },
                    items,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = SafeErrorText(ex) }, statusCode: 503);
            }
        });

        /* 复核：把这道题移出队列，直到又多了 QualityRecheckAttempts 次作答。 */
        app.MapPost("/api/admin/quality/ack", async (HttpContext context, QuestionStatsStore stats) =>
        {
            if (!IsAdminRequest(context)) return Results.Json(new { ok = false, error = "admin only" }, statusCode: 403);
            Dictionary<string, object?> body;
            try
            {
                body = await ReadJsonBody(context.Request);
            }
            catch (InvalidOperationException)
            {
                return Results.Json(new { ok = false, error = "请求体不是合法的 JSON" }, statusCode: 400);
            }
            if (!int.TryParse(GetBodyString(body, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return Results.Json(new { ok = false, error = "id 无效" }, statusCode: 400);
            }
            try
            {
                var attempts = stats.AckQuality(id, SessionUser(context) ?? "");
                if (attempts <= 0) return Results.Json(new { ok = false, error = "这道题还没有统计" }, statusCode: 404);
                return Results.Json(new { ok = true, id, attempts });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = SafeErrorText(ex) }, statusCode: 503);
            }
        });
    }

    /// <summary>
    /// 质量判定：样本够才判，两个信号分开给。
    /// · low_rate：错得太多（≥10 次作答且错 ≥60%）—— 题太难，或答案/题干本身有问题；
    /// · flat_options：选项分布过均（≥20 次、≥3 个选项、最高两项各占 ≤55% 且相差 ≤5%）——
    ///   「两个选项都像对的」这种歧义题的典型形态。
    /// </summary>
    private static List<string> QualityReasons(int attempts, int wrong, IReadOnlyDictionary<string, int> options)
    {
        var reasons = new List<string>();
        if (attempts <= 0) return reasons;
        var rate = wrong / (double)attempts;
        if (attempts >= QuestionStatsStore.QualityMinAttempts && rate >= QuestionStatsStore.QualityLowRate)
        {
            reasons.Add("low_rate");
        }
        if (attempts >= QuestionStatsStore.QualityFlatMinAttempts && options.Count >= 3)
        {
            var total = options.Values.Sum();
            if (total > 0)
            {
                var ordered = options.Values.OrderByDescending(value => value).ToList();
                var top = ordered[0] / (double)total;
                var second = ordered[1] / (double)total;
                if (top <= QuestionStatsStore.QualityFlatTopShare && top - second <= QuestionStatsStore.QualityFlatGap)
                {
                    reasons.Add("flat_options");
                }
            }
        }
        return reasons;
    }

    /// <summary>
    /// 解析 <c>{"items":[{"q":101,"ok":true,"c":"A"}]}</c>。宽容一点：<c>ok</c> 接受 true/false、1/0、"1"/"0"/"true"/"false"，
    /// 因为老客户端可能把布尔压成数字。解析不了的**整条丢掉**——尤其不能把认不出的 ok 当成「答错」，
    /// 那会给统计里塞进假错题；一条脏数据也不该废掉整批。
    /// <c>c</c>（选了哪个选项）是可选的：老客户端不带它，只是不参与选项分布统计。
    /// </summary>
    private static List<(int QuestionId, bool Correct, string? Choice)> ParseItems(string body)
    {
        var result = new List<(int, bool, string?)>();
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
            bool? correct = ok.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => ok.GetInt32() != 0,
                JsonValueKind.String => ok.GetString() switch
                {
                    "1" or "true" or "True" => true,
                    "0" or "false" or "False" => false,
                    _ => null,
                },
                _ => null,
            };
            if (correct is null) continue;
            string? choice = null;
            if (element.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.String)
            {
                choice = c.GetString();
            }
            result.Add((id, correct.Value, choice));
        }
        return result;
    }
}
