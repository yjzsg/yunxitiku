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

/// <summary>
/// 检查课程在上游有没有更新：用"增量水位"接口各探一次章节与题目（1~2 次 SOAP/课）。
/// 已知盲区：上游章节改名不更新 <c>dchangedate</c>，这类改动探不出来（客户端同样有这个问题）。
///
/// ⚠️ 两个接口的**载荷格式不一样**，各用各的解析器（这与 PullEngine 的读路径保持一致，
/// 也是这个类原来最大的 bug）：
///   · LoadNewCourseChapter_HadDistrice        → .NET DataTable XML（<see cref="Puller.JkdParsers.ParseDataTable"/>）
///   · LoadNewCourseSubjectGroup_HadDistrict   → pipe 文本（<see cref="Puller.JkdParsers.ParsePipeGroups"/>）
/// 旧实现对题目那份用了 ParseDataTable，解析必然抛 → 被 CountRows 的 catch 吞成 0 行 →
/// "只有题目更新、章节没变"时报"已是最新"，实际拉取被跳过。
/// </summary>
static class CourseUpdateChecker
{
    public static async Task<List<BankUpdateCheck>> CheckAsync(QuestionBank bank, List<int> courseIds, IConfiguration configuration)
    {
        var results = new List<BankUpdateCheck>();
        if (courseIds.Count == 0) return results;
        foreach (var courseId in courseIds)
        {
            results.Add(await CheckOneAsync(bank, courseId, configuration));
        }
        return results;
    }

    /// <summary>
    /// 单门课的检查。**任何异常都转成 <see cref="BankUpdateState.Unknown"/>**，
    /// 绝不转成"没有更新"（旧实现把检查失败封装成 hasUpdate=false，网络抖一下就被判"已是最新"、
    /// 更新按钮点了等于没点）。
    /// </summary>
    public static async Task<BankUpdateCheck> CheckOneAsync(QuestionBank bank, int courseId, IConfiguration configuration)
    {
        try
        {
            // 账号从配置/环境变量来（不再内置在代码里）
            var user = Puller.BankCredentials.ResolveUser(configuration["App:BankServiceUser"]);
            var password = Puller.BankCredentials.ResolvePassword(configuration["App:BankServicePassword"]);
            Puller.BankCredentials.EnsureConfigured(user, password);
            using var soap = new Puller.SoapClient(
                Puller.BankCredentials.ResolveUrl(configuration["App:BankServiceUrl"]), user, password, 30000);

            var marks = bank.CourseWatermarks(courseId);
            var chapterSince = string.IsNullOrWhiteSpace(marks.Chapter) ? "1900-01-01 00:00:00.999" : marks.Chapter!;
            var subjectSince = string.IsNullOrWhiteSpace(marks.Subject) ? "1900-01-01 00:00:00.999" : marks.Subject!;
            var chapterResp = await soap.InvokeAsync("LoadNewCourseChapter_HadDistrice",
                $"<courseId>{courseId}</courseId><courseType>11</courseType><district /><lastUpdateDate>{chapterSince}</lastUpdateDate>");
            var chapterRows = CountChapterRows(chapterResp.Primary);
            var subjectResp = await soap.InvokeAsync("LoadNewCourseSubjectGroup_HadDistrict",
                $"<courseId>{courseId}</courseId><courseType>1</courseType><district /><lastUpdateDate>{subjectSince}</lastUpdateDate>");
            var subjectRows = CountSubjectRows(subjectResp.Primary);
            return chapterRows > 0 || subjectRows > 0
                ? BankUpdateCheck.Updated(chapterRows, subjectRows)
                : BankUpdateCheck.Unchanged();
        }
        catch (Exception ex)
        {
            return BankUpdateCheck.Unknown(SafeErrorText(ex));
        }
    }

    /// <summary>
    /// 单个题库「有没有更新」。给 update-bank 做拉取前守卫用。
    /// **只在确定无更新时返回 false**；拿不到结论（Unknown）返回 true —— 宁可多拉一次，
    /// 也不能因为检查环节自身出错就永远拉不了（这也是这个方法注释里原本的意图）。
    /// </summary>
    public static async Task<bool> HasUpdateAsync(QuestionBank bank, int courseId, IConfiguration configuration)
    {
        var check = await CheckOneAsync(bank, courseId, configuration);
        if (check.State == BankUpdateState.Unknown)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [update-bank] 课程 {courseId} 的更新检查拿不到结论，按「可能有更新」处理，照拉：" +
                              (check.Error ?? ""));
        }
        return !check.IsCertainlyUnchanged;
    }

    /// <summary>
    /// 给前端的形状：**保留 hasUpdate**（老前端/老调用方据此决定"要不要拉"，未知时给 true =
    /// 保守照拉），同时加一个 <c>state</c> 让调用方能区分「确定没更新」与「没结论」。
    /// </summary>
    internal static object ToJson(int courseId, BankUpdateCheck check) => check.State switch
    {
        BankUpdateState.Updated => (object)new
        {
            courseId,
            state = "updated",
            hasUpdate = true,
            chapters = check.Chapters,
            subjects = check.Subjects,
        },
        BankUpdateState.Unchanged => new
        {
            courseId,
            state = "unchanged",
            hasUpdate = false,
            chapters = 0,
            subjects = 0,
        },
        _ => new
        {
            courseId,
            state = "unknown",
            hasUpdate = true,      // 未知 ≠ 无更新
            unknown = true,
            chapters = 0,
            subjects = 0,
            error = check.Error ?? "",
        },
    };

    /// <summary>按请求顺序把检查结果转成响应对象（CheckAsync 的结果与 courseIds 一一对应）。</summary>
    internal static List<object> Describe(List<int> courseIds, List<BankUpdateCheck> checks)
    {
        var results = new List<object>();
        for (var i = 0; i < courseIds.Count && i < checks.Count; i++)
        {
            results.Add(ToJson(courseIds[i], checks[i]));
        }
        return results;
    }

    /// <summary>
    /// 章节增量：.NET DataTable XML。"没有更新"时上游不返回 DataTable（载荷为空）→ 0 行。
    /// 载荷非空却解析不了 → **抛**（格式变了，不能当 0 行 = 已是最新）。
    /// </summary>
    internal static int CountChapterRows(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return 0;
        return Puller.JkdParsers.ParseDataTable(payload).Count;
    }

    /// <summary>
    /// 题目增量：pipe 文本（"start,end,chapterid,type,iindex,score,anscount|..."）。
    /// 空载荷 / "[]" / "0" / "null" 才是真的 0 行；非空但一组都解析不出来 = 格式变了 → 抛。
    /// </summary>
    internal static int CountSubjectRows(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return 0;
        var trimmed = payload.Trim();
        if (trimmed is "[]" or "0" or "null") return 0;
        var groups = Puller.JkdParsers.ParsePipeGroups(trimmed);
        if (groups.Count == 0)
        {
            throw new InvalidOperationException(
                "LoadNewCourseSubjectGroup_HadDistrict 的题目增量不是已知的 pipe 格式（一组都解析不出来），无法判断有没有更新");
        }
        return groups.Sum(group => group.Count);
    }
}
