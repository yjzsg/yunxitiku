using System.Collections.Concurrent;
using System.Globalization;

namespace JkdWeb.BankPuller;

/// <summary>
/// 拉取过程中的一个阶段性进度点。<paramref name="Current"/> / <paramref name="Total"/> 是该阶段内部的
/// 精确计数（例如「第 3/32 批题目」「第 1204/2604 张图」），<paramref name="Total"/> 为 0 表示该阶段
/// 不可细分（只有开始/结束）。
/// </summary>
public sealed record PullProgress(string Stage, int Current, int Total, string? Detail = null)
{
    /// <summary>阶段的中文名，给界面用。</summary>
    public static string Label(string stage) => stage switch
    {
        "queued" => "排队中",
        "starting" => "准备中",
        "auth" => "连接服务器",
        "chapters" => "读取章节",
        "subject-ids" => "读取题目清单",
        "subjects" => "下载题目",
        "clean-ads" => "清洗广告",
        "images" => "下载题图",
        "write" => "写入数据库",
        "done" => "完成",
        "failed" => "失败",
        _ => stage,
    };
}

/// <summary>
/// 把各阶段折算成 0~100 的总进度。
///
/// 注意这是**估算**：两个大头（下载题目、下载题图）的耗时占比随课程差异很大
/// （题多的课题目阶段占主导，图多的课题图阶段占主导），而题图总数要到题目下完才知道，
/// 所以只能给固定的阶段权重。界面上的阶段名与计数是精确的，百分比仅作参考。
/// </summary>
public static class PullProgressMath
{
    // 阶段 -> (起 %, 止 %)
    private static readonly (string Stage, double From, double To)[] Phases =
    {
        ("queued",       0,   0),
        ("starting",     0,   1),
        ("auth",         1,   3),
        ("chapters",     3,   7),
        ("subject-ids",  7,  10),
        ("subjects",    10,  62),
        ("clean-ads",   62,  64),
        ("images",      64,  98),
        ("write",       98, 100),
        ("done",       100, 100),
        ("failed",       0,   0),
    };

    public static double Percent(string stage, int current, int total)
    {
        var phase = Phases.FirstOrDefault(p => p.Stage == stage);
        if (phase.Stage is null) return 0;
        if (phase.Stage is "done") return 100;
        if (phase.Stage is "failed") return 0;

        var span = phase.To - phase.From;
        if (total <= 0) return phase.From;              // 不可细分：停在阶段起点
        var ratio = Math.Clamp(current / (double)total, 0, 1);
        return Math.Round(phase.From + span * ratio, 1);
    }
}

/// <summary>
/// 进程内的拉取进度登记处：网站发起拉取后，管理页轮询进度端点读这里。
///
/// 只存在内存里——进度是瞬时状态，重启即失效，不值得落盘。
/// 按课程 ID 记一条；同一门课重复发起会被 <see cref="Begin"/> 拒绝
/// （<see cref="PullAlreadyRunningException"/>）——两条记录共用一个 courseId，
/// 让它们并行只会互相覆盖进度，写入侧本来也要排队（见 BankWriteGate）。
/// </summary>
public sealed class PullProgressTracker
{
    /// <summary>结束后保留多久（秒），让最后一次轮询还能读到收尾状态。</summary>
    private const int KeepFinishedSeconds = 600;

    public static PullProgressTracker Shared { get; } = new();

    private sealed class Entry
    {
        public int CourseId;
        public string? Label;
        public string Stage = "starting";
        public int Current;
        public int Total;
        public string? Detail;
        public DateTime StartedAt = DateTime.Now;
        public DateTime UpdatedAt = DateTime.Now;
        public bool Finished;
        public bool Ok;
        public string? Error;
        public double Percent;
    }

    private readonly ConcurrentDictionary<int, Entry> _entries = new();

    /// <summary>只护住「查重 + 登记」这一步（微秒级），不罩住拉取本身。</summary>
    private readonly object _claimGate = new();

    /// <summary>
    /// 开始一次拉取。用完调 <see cref="PullScope.Complete"/>；忘了调（异常路径）由
    /// <see cref="PullScope.Dispose"/> 按失败收尾，绝不留「永远进行中」的僵尸记录。
    ///
    /// <paramref name="queuedAhead"/> &gt; 0 表示这次还要排在写闸门口等（前面有几个任务），
    /// 登记出来就是「排队中」——不然排队那段时间界面上什么也看不到。
    /// </summary>
    public PullScope Begin(int courseId, string? label = null, int queuedAhead = 0)
    {
        Prune();
        var entry = new Entry { CourseId = courseId, Label = label };
        SetQueued(entry, queuedAhead);
        entry.Percent = PullProgressMath.Percent(entry.Stage, 0, 0);
        // 查重与登记必须原子：两个请求同时进来时只能有一个拿到这门课（另一个拿到 409 的异常）
        lock (_claimGate)
        {
            if (_entries.TryGetValue(courseId, out var running) && !running.Finished)
            {
                throw new PullAlreadyRunningException(courseId, running.Label);
            }
            _entries[courseId] = entry;
        }
        return new PullScope(this, courseId);
    }

    private static void SetQueued(Entry entry, int ahead)
    {
        if (ahead > 0)
        {
            entry.Stage = "queued";
            entry.Detail = $"前面还有 {ahead} 个任务";
        }
        else
        {
            entry.Stage = "starting";
            entry.Detail = null;
        }
        entry.Current = 0;
        entry.Total = 0;
    }

    /// <summary>
    /// 等写闸时的状态上报：<paramref name="ahead"/> 是排在前面的任务数（0 = 闸空着，马上开始）。
    /// 拉取在闸门口最长要等 15 分钟，这期间得让界面看得见「排队中 · 前面还有 N 个任务」。
    /// </summary>
    public void MarkQueued(int courseId, int ahead)
    {
        if (!_entries.TryGetValue(courseId, out var entry) || entry.Finished) return;
        SetQueued(entry, ahead);
        entry.Percent = PullProgressMath.Percent(entry.Stage, 0, 0);
        entry.UpdatedAt = DateTime.Now;
    }

    /// <summary>同一门课是不是已经在更新（含还没拿到写闸、正在排队的那种）。</summary>
    public bool IsActive(int courseId)
        => _entries.TryGetValue(courseId, out var entry) && !entry.Finished;

    /// <summary>「课程 X 正在更新中，请等它结束」——同一门课重复发起时的中文原因（路由层回 409 用）。</summary>
    public string DescribeBusy(int courseId)
    {
        var label = _entries.TryGetValue(courseId, out var entry) ? entry.Label : null;
        return $"课程 {CourseName(courseId, label)} 正在更新中，请等它结束";
    }

    private static string CourseName(int courseId, string? label)
        => string.IsNullOrWhiteSpace(label) ? courseId.ToString(CultureInfo.InvariantCulture) : label;

    /// <summary>同一门课的拉取已经在跑（含排队中）却又要开始一次：拒绝，否则两条记录共用一个 courseId 互相覆盖。</summary>
    public sealed class PullAlreadyRunningException : InvalidOperationException
    {
        public int CourseId { get; }

        public PullAlreadyRunningException(int courseId, string? label)
            : base($"课程 {CourseName(courseId, label)} 正在更新中，请等它结束")
        {
            CourseId = courseId;
        }
    }

    public void Report(int courseId, PullProgress progress)
    {
        if (!_entries.TryGetValue(courseId, out var entry)) return;
        entry.Stage = progress.Stage;
        entry.Current = progress.Current;
        entry.Total = progress.Total;
        entry.Detail = progress.Detail;
        entry.Percent = PullProgressMath.Percent(progress.Stage, progress.Current, progress.Total);
        entry.UpdatedAt = DateTime.Now;
    }

    /// <summary>拉取抛异常时调用，让界面能显示失败而不是一直转圈。</summary>
    public void Fail(int courseId, string error)
    {
        if (!_entries.TryGetValue(courseId, out var entry)) return;
        entry.Stage = "failed";
        entry.Error = error;
        entry.Finished = true;
        entry.Ok = false;
        entry.UpdatedAt = DateTime.Now;
    }

    private void Finish(int courseId, bool ok)
    {
        if (!_entries.TryGetValue(courseId, out var entry)) return;
        entry.Finished = true;
        entry.Ok = ok;
        if (ok)
        {
            entry.Stage = "done";
            entry.Percent = 100;
        }
        entry.UpdatedAt = DateTime.Now;
    }

    /// <summary>给端点用的单条快照；没有这条记录时返回 null。</summary>
    public object? Read(int courseId)
        => _entries.TryGetValue(courseId, out var entry) ? Snapshot(courseId, entry) : null;

    /// <summary>
    /// 给端点用的全部快照（按开始时间排序，先发起的在前）。
    /// 管理页刷新后据此重新接上**所有**在跑的拉取——单查一门课的接口做不到这件事。
    /// 已结束但还在保留期内的记录也在里面，调用方自己按 active 过滤。
    /// </summary>
    public List<object> ReadAll()
    {
        Prune();
        return _entries.Values
            .OrderBy(entry => entry.StartedAt)
            .ThenBy(entry => entry.CourseId)
            .Select(entry => Snapshot(entry.CourseId, entry))
            .ToList();
    }

    private static object Snapshot(int courseId, Entry e)
    {
        var elapsed = Math.Round((e.UpdatedAt - e.StartedAt).TotalSeconds, 1);
        return new
        {
            courseId,
            label = e.Label,
            active = !e.Finished,
            stage = e.Stage,
            stageLabel = PullProgress.Label(e.Stage),
            current = e.Current,
            total = e.Total,
            detail = e.Detail,
            percent = e.Percent,
            ok = e.Finished ? e.Ok : (bool?)null,
            error = e.Error,
            startedAt = e.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            elapsedSeconds = elapsed,
            updatedAt = e.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>清掉早已结束的记录，避免字典无限增长。</summary>
    private void Prune()
    {
        var cutoff = DateTime.Now.AddSeconds(-KeepFinishedSeconds);
        foreach (var pair in _entries)
        {
            if (pair.Value.Finished && pair.Value.UpdatedAt < cutoff) _entries.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>一次拉取的登记句柄。</summary>
    public sealed class PullScope : IDisposable
    {
        private readonly PullProgressTracker _owner;
        private readonly int _courseId;
        private bool _disposed;

        internal PullScope(PullProgressTracker owner, int courseId)
        {
            _owner = owner;
            _courseId = courseId;
        }

        /// <summary>拉取成功结束。</summary>
        public void Complete() => _owner.Finish(_courseId, true);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            /* 没显式 Complete（抛异常、被中断、忘了写出这个分支）就按失败收尾，
               别让界面一直转圈。走 Fail 而不是 Finish(false)：既标结束也写一个阶段名
               和原因，界面才能显示「失败 · ……」而不是停在半路的「写入数据库」。 */
            if (_owner._entries.TryGetValue(_courseId, out var e) && !e.Finished)
            {
                _owner.Fail(_courseId, e.Error ?? "更新未正常结束（没有收到完成信号）");
            }
        }
    }
}
