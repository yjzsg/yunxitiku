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

/// <summary>从用户数据整包里算今日摘要。只读，一个字节都不写。</summary>
static class PushDigestReader
{
    /// <summary>
    /// SRS 复习间隔。**必须与 public/app.js:180 的 REVIEW_INTERVAL_DAYS 逐字一致**。
    /// 这是整个推送功能唯一的逻辑双份实现：改任何一侧都要同时改另一侧，
    /// 否则推送里的「N 道到期」会和错题本里的数字对不上。
    /// 漂移由 /api/push/preview 兜住 —— 它把服务端算的数字摊开给前端并排显示。
    /// </summary>
    internal static readonly int[] ReviewIntervalDays = { 1, 2, 4, 7, 15, 30 };

    /// <summary>读失败返回 null（调用方据此**不发**，而不是当成「今天还没练」发一条错的）。</summary>
    public static async Task<PushDigest?> ComputeAsync(UserDataStore userData, string user, DateTime now)
    {
        object data;
        try
        {
            (data, _) = await userData.LoadAsync(user);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[push] 读取 {user} 的学习数据失败，本轮跳过：{ex.Message}");
            return null;
        }
        return Compute(data, now);
    }

    public static PushDigest Compute(object? data, DateTime now)
    {
        var root = ToElement(data);
        var digest = new PushDigest();
        var today = now.Date;
        var todayEnd = today.AddDays(1).AddTicks(-1);
        var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // ── 错题本：storage.wrong[qid] = {stage, count, lastReviewAt, wrongAt, at, resolved, ...}
        //    注意它在**顶层**、跨课程；courses[cid].wrong[qid] 只是 true 标记。
        if (TryGet(root, "wrong", out var wrong) && wrong.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in wrong.EnumerateObject())
            {
                var record = prop.Value;
                if (record.ValueKind != JsonValueKind.Object)
                {
                    /* 老数据里可能有布尔值/垃圾值：算「有错题」但算不出到期日，不计入 due。
                       前端 ensureWrongReviewFields 对这类值的处理等价于「今天刚错、明天到期」，
                       所以这里跳过 = 同样的结果。 */
                    digest.WrongTotal++;
                    continue;
                }
                if (Bool(record, "resolved")) continue;
                digest.WrongTotal++;
                var stage = Math.Clamp(Int(record, "stage"), 0, ReviewIntervalDays.Length - 1);
                var anchor = ParseDate(Text(record, "lastReviewAt"))
                             ?? ParseDate(Text(record, "wrongAt"))
                             ?? ParseDate(Text(record, "at"));
                if (anchor is null) continue;
                if (anchor.Value.AddDays(ReviewIntervalDays[stage]) <= todayEnd) digest.Due++;
            }
        }

        // ── 今日活动 + 连续天数：dailyActivity["YYYY-MM-DD"]（只保留 90 天）
        if (TryGet(root, "dailyActivity", out var daily) && daily.ValueKind == JsonValueKind.Object)
        {
            if (daily.TryGetProperty(todayKey, out var day) && day.ValueKind == JsonValueKind.Object)
            {
                digest.DoneToday = Int(day, "total");
                digest.CorrectToday = Int(day, "correct");
                digest.VerifiedToday = Int(day, "verified");
            }
            /* 今天有练就从今天数，没练从**昨天**数 —— 否则每天早上看到的都是「连续 0 天」。 */
            var cursor = digest.DoneToday > 0 ? today : today.AddDays(-1);
            while (digest.Streak < 400)
            {
                var key = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!daily.TryGetProperty(key, out var bucket) || bucket.ValueKind != JsonValueKind.Object) break;
                if (Int(bucket, "total") <= 0) break;
                digest.Streak++;
                cursor = cursor.AddDays(-1);
            }
            // 最近一次练过的日期（断签提醒用）。90 天窗口内找不到就当「很久没练」。
            var probe = today;
            for (var i = 0; i <= 90; i++)
            {
                var key = probe.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (daily.TryGetProperty(key, out var bucket) && bucket.ValueKind == JsonValueKind.Object && Int(bucket, "total") > 0)
                {
                    digest.DaysSinceLast = i;
                    break;
                }
                probe = probe.AddDays(-1);
            }
        }

        // ── 考试计划：plans[cid] = {courseId, examDate, dailyTarget, dailyTargetManual, dailyLog}
        if (TryGet(root, "plans", out var plans) && plans.ValueKind == JsonValueKind.Object)
        {
            foreach (var plan in plans.EnumerateObject())
            {
                if (plan.Value.ValueKind != JsonValueKind.Object) continue;
                var examDate = ParseDate(Text(plan.Value, "examDate"));
                if (examDate is not null)
                {
                    var left = (int)Math.Ceiling((examDate.Value.Date - today).TotalDays);
                    if (left >= 0 && (digest.DaysLeft < 0 || left < digest.DaysLeft)) digest.DaysLeft = left;
                }
                var target = Int(plan.Value, "dailyTarget");
                if (target > 0 && (digest.DailyTarget == 0 || target < digest.DailyTarget)) digest.DailyTarget = target;
            }
        }

        // ── 科目名：profile.lastCourseName
        if (TryGet(root, "profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
        {
            digest.CourseName = Text(profile, "lastCourseName");
        }

        return digest;
    }

    private static JsonElement ToElement(object? data) => data switch
    {
        JsonElement element => element,
        null => JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
        _ => JsonSerializer.SerializeToElement(data),
    };

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    private static string Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (value.TryGetInt32(out var number) ? number : (int)Math.Round(value.GetDouble()))
            : 0;

    private static bool Bool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && (value.ValueKind == JsonValueKind.True
               || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n != 0));

    private static DateTime? ParseDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim().Replace('/', '-');
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed;
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed) ? parsed : null;
    }
}
