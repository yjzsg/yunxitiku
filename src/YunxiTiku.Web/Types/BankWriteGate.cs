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
/// Pulls one course straight from the upstream ExamClientService into the live bank.
/// Cross-platform: no Access/Jet, no client DLLs, no PowerShell — which is what makes
/// 拉取更新 usable on the Docker/NAS deployment, not just on the Windows server build.
/// </summary>
/// <summary>
/// 题库写操作串行闸：拉取 / 整包上传 / 单课导入 / 清广告 同时只允许一个。
/// 这些操作各自"先删后写"，并发交叠会把课程清成 0 题；这里用进程内锁把它们排开。
/// </summary>
internal static class BankWriteGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>正在执行 + 正在排队等闸的写操作数。只用来告诉界面「前面还有几个任务」。</summary>
    private static int _pending;

    /// <summary>
    /// 取闸，拿不到就等（最多 15 分钟）。
    /// <paramref name="onWaiting"/> 在**开始阻塞之前**同步调用一次，参数是排在我前面的任务数
    /// （0 = 闸空着，不用等）。拉取用它把「排队中」登记进进度记录：排队是阻塞的，
    /// 不提前上报的话，前端在整个等待期间一门课的进度都看不到。
    /// </summary>
    public static IDisposable Enter(Action<int>? onWaiting = null)
    {
        var ahead = Interlocked.Increment(ref _pending) - 1;
        onWaiting?.Invoke(ahead);
        if (!Gate.Wait(TimeSpan.FromMinutes(15)))
        {
            Interlocked.Decrement(ref _pending);
            throw new InvalidOperationException("题库正在被另一个操作占用，请等它结束后重试");
        }
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref _pending);
            Gate.Release();
        }
    }
}
