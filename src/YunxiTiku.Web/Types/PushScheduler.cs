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
/// 每 30 秒扫一遍 userdata/push/*.json，到点就发。
/// 判据全部在 RunOnceAsync 里：开关 → 今天是否已发 → 是否到点 → 是否还在宽限期 → 退避。
/// </summary>
sealed class PushScheduler : BackgroundService
{
    private readonly PushConfigStore _store;
    private readonly UserDataStore _userData;
    private readonly WebPushSender _webPush;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PushScheduler(PushConfigStore store, UserDataStore userData, WebPushSender webPush)
    {
        _store = store;
        _userData = userData;
        _webPush = webPush;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            /* 启动后先等 25 秒：让题库预热、章节映射自检先跑完，别在启动瞬间抢磁盘 IO。
               顺便也避免「刚部署完重启」触发一轮立刻发送。 */
            await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        Console.WriteLine("[push] 调度器已启动：每 30 秒检查一次");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 一轮失败不能让调度器停摆 —— 明天还得发。
                Console.WriteLine($"[push] 调度轮次异常：{ex}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 跑一轮。只有调度器调用 —— 之前还有个「管理员手动触发」的入口，连同
    /// ignoreTime / onlyUser 两个参数一起删了（见 /api/push/run 处的说明）。
    /// </summary>
    public async Task<PushRunReport> RunOnceAsync(CancellationToken ct)
    {
        var report = new PushRunReport();
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.Now;
            var today = now.Date;
            var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            foreach (var (user, cfg) in _store.ReadAll())
            {
                if (!cfg.Enabled) { report.Skipped++; continue; }

                // 今天已经发过就跳过 —— 调度器每 30 秒来一次，这里是唯一的防重发闸门。
                if (cfg.LastSentDate == todayKey) { report.Skipped++; continue; }

                {
                    if (!TryParseTime(cfg.Time, out var hour, out var minute)) { report.Skipped++; continue; }
                    var target = today.AddHours(hour).AddMinutes(minute);
                    if (now < target) { report.Skipped++; continue; }
                    /* 宽限期：容器半夜重启 / 白天关机，只要在 time + graceHours 内起来就补一条；
                       超过就不补了，免得晚上十一点弹一句「今天还没练」。 */
                    if ((now - target).TotalHours > Math.Clamp(cfg.GraceHours, 0, 23)) { report.Skipped++; continue; }
                    if (cfg.NextRetryAt.Length > 0
                        && DateTime.TryParse(cfg.NextRetryAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var retryAt)
                        && now < retryAt)
                    {
                        report.Skipped++;
                        continue;
                    }
                }

                var digest = await PushDigestReader.ComputeAsync(_userData, user, now);
                if (digest is null)
                {
                    report.Failed++;
                    AddDetail(report, $"{user}：读取学习数据失败，本轮跳过");
                    continue;
                }

                // 断签了 → 连续天数的里程碑重新计。
                if (digest.Streak < cfg.MilestoneStreak) cfg.MilestoneStreak = 0;

                var message = PushMessageBuilder.Build(cfg, digest, now);
                if (message is null) { report.Skipped++; continue; }

                var (ok, error) = await PushDelivery.SendAsync(_webPush, user, cfg.Channel, message, ct);
                if (ok)
                {
                    cfg.LastSentDate = todayKey;
                    cfg.LastSentAt = NowText();
                    cfg.LastResult = "ok";
                    cfg.LastError = "";
                    cfg.FailCount = 0;
                    cfg.NextRetryAt = "";
                    if (PushMessageBuilder.IsStreakMilestone(digest.Streak))
                    {
                        cfg.MilestoneStreak = Math.Max(cfg.MilestoneStreak, digest.Streak);
                    }
                    report.Sent++;
                    AddDetail(report, $"{user}：已发送「{message.Title}」");
                    // 一天一条，写一行日志便于事后核对「到底发了没」。
                    Console.WriteLine($"[push] 已发送 {user}：{message.Title} / {message.Body}");
                }
                else
                {
                    /* 失败**不推进** lastSentDate，但要退避：否则每 30 秒重试一次，
                       渠道挂了就是一天 2800 次请求。 */
                    cfg.FailCount++;
                    cfg.LastResult = "fail";
                    cfg.LastError = error;
                    var delayMinutes = Math.Min(Math.Pow(2, Math.Min(cfg.FailCount, 5)), 32);
                    cfg.NextRetryAt = now.AddMinutes(delayMinutes).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    report.Failed++;
                    AddDetail(report, $"{user}：发送失败 —— {error}");
                }
                _store.SaveState(user, cfg);
            }
        }
        finally
        {
            _gate.Release();
        }
        return report;
    }

    private static void AddDetail(PushRunReport report, string line)
    {
        if (report.Details.Count < 50) report.Details.Add(line);
    }

    private static bool TryParseTime(string? text, out int hour, out int minute)
    {
        hour = 20;
        minute = 0;
        var match = Regex.Match((text ?? "").Trim(), @"^(\d{1,2}):(\d{2})$");
        if (!match.Success) return false;
        hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }
}
