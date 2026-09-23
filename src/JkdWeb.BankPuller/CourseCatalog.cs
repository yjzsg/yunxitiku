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

/// <summary>One row of <c>itemName=courseclass</c>（一级分类）。</summary>
public sealed record UpstreamClass(int Id, string? Name, int? Index, bool Stopped);

/// <summary>One row of <c>itemName=coursesubclass</c>（二级分类）。</summary>
public sealed record UpstreamSubClass(int Id, string? Name, int? ClassId, int? Index, bool Stopped);

/// <summary>上游课程目录的一次完整快照（课程 + 一级/二级分类）。</summary>
public sealed record CourseCatalogSnapshot(
    IReadOnlyList<UpstreamCourse> Courses,
    IReadOnlyList<UpstreamClass> Classes,
    IReadOnlyList<UpstreamSubClass> SubClasses);

/// <summary>
/// 上游的**课程目录**。这是金考典客户端"课程列表"的数据源，也是唯一能拿到
/// 课程名/分类/序号的接口 —— 按 courseId 逐个探测章节（老办法）只能知道"存不存在"，
/// 拿不到名字，而且会漏掉章节为空的课。
///
/// 调用形态：<c>LoadBaseItemUpdateNewData(itemName="course"|"courseclass"|"coursesubclass",
/// lastUpdateDate=1900-01-01 00:00:00.999)</c>，各返回一张 .NET DataTable，
/// 列名与本地同名表同源（course 约 1145 行 × 30 列）。
/// <c>itemName=coursesubjecttype</c> 会 HTTP 500，别用。
///
/// 目录变化很慢，进程内缓存一份（默认 10 分钟），避免管理端每次列课程都打这几次大响应。
/// </summary>
public static class CourseCatalog
{
    /// <summary>增量接口的哨兵日期：拉全量。空值会 HTTP 500，0001-01-01 也会 500。</summary>
    public const string FirstPullDate = "1900-01-01 00:00:00.999";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CourseCatalogSnapshot? _cache;
    private static DateTimeOffset _cachedAt;

    public static TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(10);

    public static void InvalidateCache()
    {
        _cache = null;
    }

    /// <summary>取缓存的快照；没有或已过期就重新拉。</summary>
    public static async Task<CourseCatalogSnapshot> LoadFullAsync(
        SoapClient soap, CancellationToken ct = default, bool force = false)
    {
        if (!force && IsFresh()) return _cache!;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && IsFresh()) return _cache!;
            var courses = await LoadTableAsync(soap, "course", ct).ConfigureAwait(false);
            var classes = await LoadTableAsync(soap, "courseclass", ct).ConfigureAwait(false);
            var subClasses = await LoadTableAsync(soap, "coursesubclass", ct).ConfigureAwait(false);

            var snapshot = new CourseCatalogSnapshot(
                courses.Select(row => new UpstreamCourse(
                    JkdParsers.ToInt(Get(row, "icourseid")) ?? 0,
                    Get(row, "ccoursename"),
                    JkdParsers.ToInt(Get(row, "iclassid")),
                    JkdParsers.ToInt(Get(row, "isubclassid")),
                    JkdParsers.ToInt(Get(row, "iindex")),
                    JkdParsers.ToBool(Get(row, "bstopflag")),
                    JkdParsers.ToDbDate(Get(row, "dchangedate")),
                    JkdParsers.ToDbDate(Get(row, "dchapterchange")),
                    JkdParsers.ToDbDate(Get(row, "dsubjectchange")),
                    Get(row, "ctypscount")))
                .Where(c => c.Id > 0)
                .ToList(),
                classes.Select(row => new UpstreamClass(
                    JkdParsers.ToInt(Get(row, "iclassid")) ?? 0,
                    Get(row, "ccoursecname"),
                    JkdParsers.ToInt(Get(row, "iindex")),
                    JkdParsers.ToBool(Get(row, "bstopflag"))))
                .Where(c => c.Id > 0)
                .ToList(),
                subClasses.Select(row => new UpstreamSubClass(
                    JkdParsers.ToInt(Get(row, "isubclassid")) ?? 0,
                    Get(row, "csubclassname"),
                    JkdParsers.ToInt(Get(row, "iclassid")),
                    JkdParsers.ToInt(Get(row, "iindex")),
                    JkdParsers.ToBool(Get(row, "bstopflag"))))
                .Where(c => c.Id > 0)
                .ToList());

            _cache = snapshot;
            _cachedAt = DateTimeOffset.UtcNow;
            return snapshot;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>只要课程列表的调用方（拉取层补课程行用）。</summary>
    public static async Task<List<UpstreamCourse>> LoadAsync(
        SoapClient soap, CancellationToken ct = default, bool force = false)
        => (await LoadFullAsync(soap, ct, force).ConfigureAwait(false)).Courses.ToList();

    public static async Task<UpstreamCourse?> FindAsync(
        SoapClient soap, long courseId, CancellationToken ct = default)
    {
        var all = await LoadAsync(soap, ct).ConfigureAwait(false);
        return all.FirstOrDefault(c => c.Id == courseId);
    }

    private static async Task<List<Dictionary<string, string?>>> LoadTableAsync(
        SoapClient soap, string itemName, CancellationToken ct)
    {
        var result = await soap.InvokeAsync(
            "LoadBaseItemUpdateNewData",
            $"<itemName>{itemName}</itemName><lastUpdateDate>{FirstPullDate}</lastUpdateDate>",
            ct).ConfigureAwait(false);
        return JkdParsers.ParseDataTable(result.Primary);
    }

    private static bool IsFresh()
        => _cache is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl;

    private static string? Get(Dictionary<string, string?> row, string column)
        => row.TryGetValue(column, out var value) ? value : null;
}
