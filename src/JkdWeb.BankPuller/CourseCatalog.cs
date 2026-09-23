namespace JkdWeb.BankPuller;

/// <summary>One row of the upstream course catalog (<c>LoadBaseItemUpdateNewData itemName=course</c>).</summary>
public sealed record UpstreamCourse(
    long Id,
    string? Name,
    int? ClassId,
    int? SubClassId,
    int? Index,
    bool Stopped,
    string? ChangedDate,
    string? ChapterChange,
    string? SubjectChange,
    string? TypsCount);

/// <summary>
/// 上游的**课程目录**。这是金考典客户端"课程列表"的数据源，也是唯一能拿到
/// 课程名/分类/序号的接口 —— 按 courseId 逐个探测章节（老办法）只能知道"存不存在"，
/// 拿不到名字，而且会漏掉章节为空的课。
///
/// 调用形态：<c>LoadBaseItemUpdateNewData(itemName="course", lastUpdateDate=1900-01-01 00:00:00.999)</c>，
/// 返回一张 .NET DataTable（1145 行左右，30 列），列名与本地 <c>course</c> 表同源。
///
/// 目录变化很慢，进程内缓存一份（默认 10 分钟），避免管理端每次列课程都打一次 1MB 的响应。
/// </summary>
public static class CourseCatalog
{
    /// <summary>增量接口的哨兵日期：拉全量。空值会 HTTP 500，0001-01-01 也会 500。</summary>
    public const string FirstPullDate = "1900-01-01 00:00:00.999";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<UpstreamCourse>? _cache;
    private static DateTimeOffset _cachedAt;

    public static TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(10);

    public static void InvalidateCache()
    {
        _cache = null;
    }

    /// <summary>拉取（或取缓存的）上游课程目录。</summary>
    public static async Task<List<UpstreamCourse>> LoadAsync(
        SoapClient soap, CancellationToken ct = default, bool force = false)
    {
        if (!force && IsFresh()) return _cache!;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && IsFresh()) return _cache!;
            var result = await soap.InvokeAsync(
                "LoadBaseItemUpdateNewData",
                $"<itemName>course</itemName><lastUpdateDate>{FirstPullDate}</lastUpdateDate>",
                ct).ConfigureAwait(false);
            var rows = JkdParsers.ParseDataTable(result.Primary);
            var list = new List<UpstreamCourse>(rows.Count);
            foreach (var row in rows)
            {
                var id = JkdParsers.ToInt(Get(row, "icourseid"));
                if (id is null || id <= 0) continue;
                list.Add(new UpstreamCourse(
                    id.Value,
                    Get(row, "ccoursename"),
                    JkdParsers.ToInt(Get(row, "iclassid")),
                    JkdParsers.ToInt(Get(row, "isubclassid")),
                    JkdParsers.ToInt(Get(row, "iindex")),
                    JkdParsers.ToBool(Get(row, "bstopflag")),
                    JkdParsers.ToDbDate(Get(row, "dchangedate")),
                    JkdParsers.ToDbDate(Get(row, "dchapterchange")),
                    JkdParsers.ToDbDate(Get(row, "dsubjectchange")),
                    Get(row, "ctypscount")));
            }
            _cache = list;
            _cachedAt = DateTimeOffset.UtcNow;
            return list;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<UpstreamCourse?> FindAsync(
        SoapClient soap, long courseId, CancellationToken ct = default)
    {
        var all = await LoadAsync(soap, ct).ConfigureAwait(false);
        return all.FirstOrDefault(c => c.Id == courseId);
    }

    private static bool IsFresh()
        => _cache is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl;

    private static string? Get(Dictionary<string, string?> row, string column)
        => row.TryGetValue(column, out var value) ? value : null;
}
