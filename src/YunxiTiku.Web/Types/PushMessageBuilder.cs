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

/// <summary>把摘要变成一条通知。返回 null = 今天不发。</summary>
static class PushMessageBuilder
{
    /* 里程碑阈值：7 / 30 / 60 天。
       为什么不放 100：dailyActivity 只保留 90 天（app.js:5918），连续 100 天在数据上不可达，
       写上去就是一个永远不会触发的分支。 */
    private static readonly int[] StreakMilestones = { 7, 30, 60 };

    public static PushMessage? Build(PushConfig cfg, PushDigest d, DateTime now, bool test = false)
    {
        var style = (cfg.Style ?? "milestone").Trim().ToLowerInvariant();
        var prefix = test ? "【测试】" : "";
        var url = BuildUrl(cfg.BaseUrl);

        if (d.DoneToday == 0)
        {
            /* 断签优先：「今天还没开始」对连续 5 天没打开过的人太轻了。 */
            if (d.DaysSinceLast >= 3)
            {
                return new PushMessage(prefix + $"已经 {d.DaysSinceLast} 天没练", Join($"错题堆到 {d.WrongTotal} 道", ExamText(d)), url);
            }
            if (d.Due > 0)
            {
                return new PushMessage(prefix + $"{d.Due} 道错题到期", Join($"预计 {EstimateMinutes(d.Due)} 分钟", ExamText(d)), url);
            }
            return new PushMessage(prefix + "今天还没开始", Join(SuggestText(d), ExamText(d)), url);
        }

        if (style == "remind") return null;   // 只催没练：今天练过了就一句话都不发

        // ── 里程碑（style = milestone / digest）：从稀有到常见
        if (IsStreakMilestone(d.Streak) && cfg.MilestoneStreak < d.Streak)
        {
            return new PushMessage(prefix + $"连续 {d.Streak} 天", Join($"今天 {d.DoneToday} 题", ExamText(d)), url);
        }
        /* 「到期错题清零」本来也在里程碑里，**故意去掉了**：
           一个每天认真练的人每天都能把到期清零，于是它退化成每天必发的第二条消息，
           正是这个方案要避免的通知疲劳。留「单日全对」这种真正稀有的。 */
        if (d.VerifiedToday >= 20 && d.CorrectToday == d.VerifiedToday)
        {
            return new PushMessage(prefix + $"今天 {d.VerifiedToday} 题全对", Join($"连续 {d.Streak} 天", ExamText(d)), url);
        }
        if (style == "digest")
        {
            return new PushMessage(prefix + $"今天 {d.DoneToday} 题", Join($"正确率 {RateText(d)} · 连续 {d.Streak} 天", ExamText(d)), url);
        }
        return null;
    }

    public static bool IsStreakMilestone(int streak) => Array.IndexOf(StreakMilestones, streak) >= 0;

    /// <summary>通知里「点开跳到哪里」的绝对地址；没填就是空串（渠道会忽略）。</summary>
    public static string UrlFor(string baseUrl) => BuildUrl(baseUrl);

    private static string BuildUrl(string baseUrl)
    {
        var value = (baseUrl ?? "").Trim().TrimEnd('/');
        return value.Length == 0 ? "" : value + "/?from=push";
    }

    private static string SuggestText(PushDigest d)
        => d.DailyTarget > 0 ? $"建议 {d.DailyTarget} 题" : "练几道保持手感";

    private static string ExamText(PushDigest d)
    {
        if (d.DaysLeft < 0) return "";
        if (d.DaysLeft == 0) return "今天考试";
        return $"距考试 {d.DaysLeft} 天";
    }

    private static string RateText(PushDigest d)
        => d.VerifiedToday > 0 ? $"{Math.Round(d.CorrectToday * 100.0 / d.VerifiedToday)}%" : "—";

    private static int EstimateMinutes(int count) => Math.Max(1, (int)Math.Ceiling(count * 0.5));

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
