using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace JkdWeb.BankPuller;

public sealed class PullOptions
{
    /// <summary>
    /// 上游服务地址。**留空表示"没指定"**，由 <see cref="BankCredentials.ResolveUrl"/> 按
    /// 显式值 &gt; 环境变量 JKD_BANK_URL &gt; 内置默认值 解析。
    /// （以前这里预填了内置默认值，CLI 再把"已经填好的"值传给 ResolveUrl，
    /// 于是 JKD_BANK_URL 兜底永远轮不到 —— BE-11。）
    /// </summary>
    public string ServiceUrl { get; set; } = "";

    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public long CourseId { get; set; }
    public string OutDb { get; set; } = "";
    public string AssetsRoot { get; set; } = "";
    public string? MetaDb { get; set; }

    /// <summary>full = download everything (first-download sentinel); incremental = only rows newer than the stored watermark.</summary>
    public string Mode { get; set; } = "full";

    /// <summary>union | group | full - which source(s) the subject id list comes from.</summary>
    public string SubjectSource { get; set; } = "union";

    public string? ChapterSince { get; set; }
    public string? SubjectSince { get; set; }

    /// <summary>Sentinel date that makes the server treat the call as a first download (active rows only).</summary>
    public string FullSince { get; set; } = "1900-01-01 00:00:00.999";

    public int BatchSize { get; set; } = 200;
    public bool FetchImages { get; set; } = true;

    /// <summary>flagged = only subjects whose row says it has a picture; all = ask for every subject; none = skip.</summary>
    public string ImageMode { get; set; } = "flagged";

    /// <summary>
    /// Prefix written into the img src when a {图} placeholder is resolved.
    /// The site rewrites src="data/..." (and ".../jinkaodian/data/") to src="assets/...",
    /// so any of those forms works; the default is the platform-neutral relative form.
    /// </summary>
    public string ImageSrcPrefix { get; set; } = "";

    public bool SkipEmpty { get; set; }

    /// <summary>Strip the promotional text the upstream service splices into descriptions (default on).</summary>
    public bool CleanAds { get; set; } = true;
    public int? MaxSubjects { get; set; }

    /// <summary>
    /// 整课替换：删掉该课旧正文再写新的（数据库事务保证"一起提交"，**不保证集合完整**）。
    /// 默认 true —— 注意与 <see cref="Mode"/>="incremental" 组合会删掉没下载的部分，
    /// 所以增量模式必须配 <see cref="ReplaceCourse"/>=false（upsert），见 <see cref="DescribePartialFetch"/>。
    /// </summary>
    public bool ReplaceCourse { get; set; } = true;

    /// <summary>
    /// 是否允许"残缺/部分"的整课替换。默认 false（拒绝提交）：
    /// 缺题、上游坏题、部分拉取（incremental / --max-subjects / 非 union 题号源 / 显式水位）
    /// 一律**在删旧数据之前**失败，旧内容一行不动。要接受部分更新必须显式打开
    /// （CLI: --allow-partial-replace），且此时水位不会推进（否则下次增量拉取会跳过缺口）。
    /// </summary>
    public bool AllowPartialReplace { get; set; }

    /// <summary>SOAP 单次调用最多尝试次数（含首次）。属请求预算，调试/测试可调小。</summary>
    public int SoapMaxAttempts { get; set; } = 4;

    /// <summary>SOAP 重试的基础退避毫秒数（第 n 次重试前等 n × 该值）。</summary>
    public int SoapRetryDelayMs { get; set; } = 1500;

    public bool DryRun { get; set; }
}

public sealed class PullReport
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public long CourseId { get; set; }
    public string Mode { get; set; } = "";
    public string SubjectSource { get; set; } = "";
    public string ServiceUrl { get; set; } = "";
    public string OutDb { get; set; } = "";
    public string AssetsRoot { get; set; } = "";
    public bool AuthVerified { get; set; }
    public long? AuthProbeChapterIds { get; set; }
    public int SoapCalls { get; set; }
    public int SoapRetries { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? SoftwareVersion { get; set; }
    public ChapterStats Chapters { get; set; } = new();
    public SubjectStats Subjects { get; set; } = new();
    public ImageStats Images { get; set; } = new();
    public AdStats Ads { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Result of the built-in ad cleaning pass (see <see cref="AdCleaner"/>).</summary>
public sealed class AdStats
{
    public bool Enabled { get; set; }
    public int SubjectsCleaned { get; set; }
    public int AdHits { get; set; }
    public int CharsRemoved { get; set; }
}

public sealed class ChapterStats
{
    public string UsedSince { get; set; } = "";
    public string? NewWatermark { get; set; }
    public int Downloaded { get; set; }
    public int Written { get; set; }
    public int Stopped { get; set; }
    public int DbRowsAfter { get; set; }
}

public sealed class SubjectStats
{
    public string UsedSince { get; set; } = "";
    public string? NewWatermark { get; set; }
    public int IdsFromGroup { get; set; }
    public int IdsFromFullList { get; set; }
    public int IdsSelected { get; set; }
    public int RowsReturned { get; set; }
    public int RowsMissing { get; set; }
    public int SkippedEmpty { get; set; }
    public int Written { get; set; }
    public int Stopped { get; set; }
    public int WrongCourse { get; set; }
    public int DbRowsAfter { get; set; }
    public int WithPictureFlag { get; set; }
    public List<long> MissingSample { get; set; } = new();

    /// <summary>
    /// 缩小到单题后上游仍然只对**这一题**返回 HTTP 500 的题号（二分定位得出）：
    /// 属上游数据缺陷，跳过该题不算"拉取层失败"。网络故障 / 鉴权 / 协议错误**不会**进这里
    /// —— 它们让整次拉取失败，绝不摊成"坏题"。
    /// </summary>
    public List<long> BadIds { get; set; } = new();
}

public sealed class ImageStats
{
    public string Mode { get; set; } = "";
    public int SubjectsQueried { get; set; }
    public int SubjectsWithImageList { get; set; }
    public int FilesWritten { get; set; }
    /// <summary>单张图落盘失败（权限/瞬时 IO 故障）的次数；失败不中断整门课，只记警告。</summary>
    public int FilesFailed { get; set; }
    public List<string> FailedSample { get; set; } = new();
    public long BytesWritten { get; set; }
    /// <summary>Files the server returned in a power-of-two buffer with trailing zero padding removed.</summary>
    public int PaddedFilesTrimmed { get; set; }
    public long PaddedBytesTrimmed { get; set; }
    public int EmptyPayloads { get; set; }
    public int PlaceholdersReplaced { get; set; }
    public int PlaceholdersUnresolved { get; set; }
    public int SubjectsWithUnresolved { get; set; }
    public List<string> UnknownTableNames { get; set; } = new();
}

/// <summary>
/// Cross-platform replacement for scripts/pull-bank-update.ps1:
/// talks to the ASMX service directly over HTTP and writes SQLite + assets.
/// No Access/Jet, no client DLLs, no PowerShell, no Windows.
/// </summary>
public sealed class PullEngine
{
    private const int ChapterCourseType = 11;
    private const int SubjectCourseType = 1;
    private const string ImageTablePrefix = "coursesubject";

    private static readonly Regex PlaceholderRegex = new(@"\{图(\d*)\}", RegexOptions.Compiled);

    private readonly Action<string> _log;
    private readonly Action<PullProgress> _progress;

    public PullEngine(Action<string>? log = null, Action<PullProgress>? progress = null)
    {
        _log = log ?? (_ => { });
        _progress = progress ?? (_ => { });
    }

    /// <summary>上报一个阶段性进度点（界面进度条用；CLI 不传回调时是空操作）。</summary>
    private void Report(string stage, int current = 0, int total = 0, string? detail = null)
        => _progress(new PullProgress(stage, current, total, detail));

    public async Task<PullReport> RunAsync(PullOptions o, CancellationToken ct = default)
    {
        // 账号不再内置在代码里，缺了就直接给出带指引的错误
        BankCredentials.EnsureConfigured(o.User, o.Password);

        // 服务地址在这里解析：显式 > JKD_BANK_URL > 内置默认（调用方传空 = 没指定，不许预填默认值）
        var serviceUrl = BankCredentials.ResolveUrl(o.ServiceUrl);

        Report("starting", 0, 0, $"课程 {o.CourseId}");

        var report = new PullReport
        {
            CourseId = o.CourseId,
            Mode = o.Mode,
            SubjectSource = o.SubjectSource,
            ServiceUrl = serviceUrl,
            OutDb = Path.GetFullPath(o.OutDb),
            AssetsRoot = string.IsNullOrEmpty(o.AssetsRoot) ? "" : Path.GetFullPath(o.AssetsRoot),
            SoftwareVersion = typeof(PullEngine).Assembly.GetName().Version?.ToString(),
        };
        report.Ads.Enabled = o.CleanAds;
        var sw = Stopwatch.StartNew();

        using var soap = new SoapClient(serviceUrl, o.User, o.Password)
        {
            MaxAttempts = Math.Max(1, o.SoapMaxAttempts),
            RetryDelayMs = Math.Max(0, o.SoapRetryDelayMs),
        };
        try
        {
            await RunCoreAsync(o, report, soap, ct).ConfigureAwait(false);
            report.Ok = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消是控制流，不是"报告里的一条错误"：记日志后原样抛出，让取消语义穿透到调用方
            // （否则 BankPull/界面会把它重新包装成普通失败，见 BE-10）。报告对象在这里就丢了，
            // 所以不设"Canceled 字段"，取消只由异常本身表达。
            _log("CANCELED: 拉取已取消：数据库未提交，旧内容与水位保持原样");
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // 没人请求取消却收到取消：按网络/超时口径记成失败（SoapClient 已把 deadline 归进网络类，
            // 走到这里说明取消来自上游或别处信号）。
            report.Ok = false;
            report.Error = "拉取被中断（非调用方取消）: " + ex.Message;
            _log("ERROR: " + report.Error);
        }
        catch (Exception ex)
        {
            report.Ok = false;
            report.Error = ex.Message;
            _log("ERROR: " + ex.Message);
        }
        finally
        {
            sw.Stop();
            report.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
            report.SoapCalls = soap.CallCount;
            report.SoapRetries = soap.RetryCount;
        }
        return report;
    }

    private async Task RunCoreAsync(PullOptions o, PullReport report, SoapClient soap, CancellationToken ct)
    {
        if (o.CourseId <= 0) throw new ArgumentException("--course is required");

        // dry-run 的契约是"零写入"（CLI 帮助原文：query only, write nothing）：先把落点定下来。
        // 真跑 → 题图写线上课程目录；dry run → 题图只写随机临时 staging，跑完删掉；
        // 两种情况下题库文件都只在最后一步（事务）里被碰。任何目录创建/图片覆写都不许发生在
        // DryRun 判断之前（BE-11）。
        var dryRunStage = o.DryRun
            ? Path.Combine(Path.GetTempPath(),
                "jkd-dryrun-" + o.CourseId.ToString(CultureInfo.InvariantCulture) + "-" +
                Guid.NewGuid().ToString("N")[..8])
            : null;

        // ---- 0. auth probe (missing/invalid MGSoapHeader -> server returns empty with HTTP 200) ----
        var probe = await soap.InvokeAsync("LoadCouseFullChapterIdList",
            $"<courseId>{o.CourseId}</courseId>", ct).ConfigureAwait(false);
        var probeRows = JkdParsers.ParseDataTable(probe.Primary);
        report.AuthProbeChapterIds = probeRows.Count;
        report.AuthVerified = probeRows.Count > 0;
        _log($"auth probe: LoadCouseFullChapterIdList({o.CourseId}) -> {probeRows.Count} ids");
        Report("auth", 1, 1);
        if (!report.AuthVerified)
        {
            throw new InvalidOperationException(
                "auth probe returned 0 ids - SOAP auth header rejected (or the course is empty). " +
                "The server silently returns empty datasets when the MGSoapHeader password is wrong.");
        }

        // ---- 1. chapters ----
        var chSince = o.ChapterSince
            ?? (o.Mode == "full" ? o.FullSince : ReadWatermark(o, "dchapterchange") ?? o.FullSince);
        report.Chapters.UsedSince = chSince;
        _log($"chapters: since '{chSince}'");
        Report("chapters", 0, 1);
        var chResp = await soap.InvokeAsync("LoadNewCourseChapter_HadDistrice",
            $"<courseId>{o.CourseId}</courseId><courseType>{ChapterCourseType}</courseType>" +
            $"<district /><lastUpdateDate>{chSince}</lastUpdateDate>", ct).ConfigureAwait(false);
        var chRows = JkdParsers.ParseDataTable(chResp.Primary);
        report.Chapters.NewWatermark = chResp.GetExtra("lastUpdateDate");
        report.Chapters.Downloaded = chRows.Count;
        var chapters = chRows.Select(r => MapChapter(r, o.CourseId, report)).ToList();
        report.Chapters.Stopped = chapters.Count(c => c.StopFlag != 0);
        _log($"chapters: downloaded {chapters.Count} (stopped {report.Chapters.Stopped}), " +
             $"new watermark '{report.Chapters.NewWatermark}'");
        Report("chapters", 1, 1, $"{chapters.Count} 章");

        // ---- 2. subject id set ----
        var subSince = o.SubjectSince
            ?? (o.Mode == "full" ? o.FullSince : ReadWatermark(o, "dsubjectchange") ?? o.FullSince);
        report.Subjects.UsedSince = subSince;
        Report("subject-ids", 0, 1);

        var grpResp = await soap.InvokeAsync("LoadNewCourseSubjectGroup_HadDistrict",
            $"<courseId>{o.CourseId}</courseId><courseType>{SubjectCourseType}</courseType>" +
            $"<district /><lastUpdateDate>{subSince}</lastUpdateDate>", ct).ConfigureAwait(false);
        var groups = JkdParsers.ParsePipeGroups(grpResp.Primary);
        var idsFromGroup = new List<long>();
        foreach (var g in groups)
        {
            foreach (var (id, _) in g.Expand()) idsFromGroup.Add(id);
        }
        report.Subjects.NewWatermark = grpResp.GetExtra("lastUpdateDate");
        report.Subjects.IdsFromGroup = idsFromGroup.Distinct().Count();
        _log($"subjects: group list -> {groups.Count} groups / {report.Subjects.IdsFromGroup} ids " +
             $"(new watermark '{report.Subjects.NewWatermark}')");

        var idsFromFull = new List<long>();
        if (o.SubjectSource is "union" or "full")
        {
            var fullResp = await soap.InvokeAsync("LoadCouseFullSubjectIdList",
                $"<courseId>{o.CourseId}</courseId><courseType>{SubjectCourseType}</courseType>", ct)
                .ConfigureAwait(false);
            foreach (var r in JkdParsers.ParseDataTable(fullResp.Primary))
            {
                if (JkdParsers.ToInt(r.GetValueOrDefault("isubjectid")) is { } v) idsFromFull.Add(v);
            }
            report.Subjects.IdsFromFullList = idsFromFull.Distinct().Count();
            _log($"subjects: full id list -> {report.Subjects.IdsFromFullList} ids");
        }

        var wanted = o.SubjectSource switch
        {
            "group" => idsFromGroup,
            "full" => idsFromFull,
            _ => idsFromGroup.Concat(idsFromFull).ToList(),
        };
        var ids = wanted.Distinct().ToList();
        ids.Sort();
        if (o.MaxSubjects is { } cap && ids.Count > cap)
        {
            ids = ids.Take(cap).ToList();
            report.Notes.Add($"--max-subjects {cap} applied");
        }
        report.Subjects.IdsSelected = ids.Count;
        _log($"subjects: selected {ids.Count} ids to download");
        Report("subject-ids", 1, 1, $"{ids.Count} 题");

        // ---- 3. download subject rows in batches ----
        var subjects = new List<SubjectRow>();
        var returnedIds = new HashSet<long>();
        var batches = (int)Math.Ceiling(ids.Count / (double)o.BatchSize);
        Report("subjects", 0, batches, $"{ids.Count} 题 / {batches} 批");
        for (var i = 0; i < ids.Count; i += o.BatchSize)
        {
            var batch = ids.Skip(i).Take(o.BatchSize).ToList();
            var rows = await FetchSubjectBatchAsync(soap, o.CourseId, batch, report, ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var row = MapSubject(r, o.CourseId, report);
                if (row is null) continue;
                returnedIds.Add(row.SubjectId);
                if (row.CourseId != o.CourseId) report.Subjects.WrongCourse++;

                // Strip upstream promotional text before anything downstream sees it, so both
                // placeholder resolution and the written rows operate on clean content.
                if (o.CleanAds)
                {
                    var (cleanedRow, adHits, adCharsRemoved) = AdCleaner.CleanRow(row);
                    if (adHits > 0)
                    {
                        row = cleanedRow;
                        report.Ads.SubjectsCleaned++;
                        report.Ads.AdHits += adHits;
                        report.Ads.CharsRemoved += adCharsRemoved;
                    }
                }

                if (o.SkipEmpty && IsEmptyContent(row))
                {
                    report.Subjects.SkippedEmpty++;
                    continue;
                }
                if (row.HasPictureFlag) report.Subjects.WithPictureFlag++;
                subjects.Add(row);
            }
            _log($"  batch {i / o.BatchSize + 1}/{batches}: asked {batch.Count}, got {rows.Count}");
            Report("subjects", i / o.BatchSize + 1, batches, $"{subjects.Count} 题");

            // 坏题数量异常大 → 更像上游整体故障而不是"个别题数据缺陷"：立刻中止，
            // 别让二分把每一批都摊成几百次请求（BE-10/BE-15 的请求预算）。
            var tolerance = BadIdTolerance(ids.Count);
            if (report.Subjects.BadIds.Count > tolerance)
            {
                throw new SoapCallException(SoapFailureKind.Upstream, "LoadCourseSubjectListInfo",
                    $"上游对 {report.Subjects.BadIds.Count} 个题号持续返回 HTTP 500（超过容忍上限 {tolerance}）：" +
                    "判定为上游整体故障，已中止本次拉取，未写入任何数据。");
            }
        }

        report.Subjects.RowsReturned = returnedIds.Count;
        var missing = ids.Where(id => !returnedIds.Contains(id)).ToList();
        report.Subjects.RowsMissing = missing.Count;
        report.Subjects.MissingSample = missing.Take(20).ToList();
        report.Subjects.Stopped = subjects.Count(s => s.StopFlag != 0);
        if (missing.Count > 0)
        {
            report.Warnings.Add($"{missing.Count} requested subject ids came back with no row " +
                                $"(sample: {string.Join(",", report.Subjects.MissingSample)})" +
                                (o.ReplaceCourse && !o.AllowPartialReplace
                                    ? " —— 集合不完整，整课替换会被拒绝（旧内容保持不动）"
                                    : ""));
        }
        if (report.Subjects.BadIds.Count > 0)
        {
            _log($"subjects: {report.Subjects.BadIds.Count} subject(s) rejected by upstream, skipped: " +
                 $"{string.Join(",", report.Subjects.BadIds.Take(20))}");
        }
        _log($"subjects: rows returned {report.Subjects.RowsReturned}, missing {report.Subjects.RowsMissing}, " +
             $"with picture flag {report.Subjects.WithPictureFlag}");
        if (o.CleanAds)
        {
            _log($"clean-ads: removed {report.Ads.AdHits} ad segments from {report.Ads.SubjectsCleaned} subjects " +
                 $"({report.Ads.CharsRemoved} chars)");
        }

        // ---- 4. images + inline placeholder resolution ----
        if (o.FetchImages && !string.IsNullOrEmpty(o.AssetsRoot) && o.ImageMode != "none")
        {
            var srcPrefix = string.IsNullOrEmpty(o.ImageSrcPrefix)
                ? $"data/{o.CourseId.ToString(CultureInfo.InvariantCulture)}/"
                : o.ImageSrcPrefix!;
            // dry run 只写随机临时 staging —— 不创建、也不覆写线上题图目录（BE-11）
            var imageRoot = dryRunStage ?? o.AssetsRoot;
            var dir = Path.Combine(imageRoot, o.CourseId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);

            var targets = o.ImageMode == "all"
                ? subjects.Select(s => s.SubjectId).ToHashSet()
                : subjects.Where(s => s.HasPictureFlag).Select(s => s.SubjectId).ToHashSet();
            report.Images.Mode = o.ImageMode;
            _log($"images: mode={o.ImageMode}, querying {targets.Count} subjects, src prefix '{srcPrefix}'");
            Report("images", 0, targets.Count, $"{targets.Count} 题有图");

            var updated = new List<SubjectRow>(subjects.Count);
            var imagesDone = 0;
            foreach (var s in subjects)
            {
                if (!targets.Contains(s.SubjectId)) { updated.Add(s); continue; }

                ct.ThrowIfCancellationRequested();
                report.Images.SubjectsQueried++;
                imagesDone++;
                Report("images", imagesDone, targets.Count, $"{report.Images.FilesWritten} 张已落盘");
                var resp = await soap.InvokeAsync("LoadSubjectImageList",
                    $"<subjectId>{s.SubjectId}</subjectId>", ct).ConfigureAwait(false);
                var rows = JkdParsers.ParseDataTable(resp.Primary);
                if (rows.Count == 0)
                {
                    report.Images.EmptyPayloads++;
                    updated.Add(s);
                    continue;
                }
                report.Images.SubjectsWithImageList++;

                // block -> { index -> (fileName, ext) }
                var blocks = new SortedDictionary<string, Dictionary<int, (string Name, string Ext)>>(StringComparer.Ordinal);
                foreach (var r in rows)
                {
                    var tableName = r.GetValueOrDefault("ctblname") ?? "";
                    var ext = SanitizeImageExt(r.GetValueOrDefault("cextname"));
                    var b64 = r.GetValueOrDefault("pimage");
                    if (string.IsNullOrEmpty(b64)) continue;

                    var suffix = tableName;
                    if (suffix.StartsWith(ImageTablePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        suffix = suffix[ImageTablePrefix.Length..];
                    }
                    else if (suffix.Length > 0)
                    {
                        lock (report.Images.UnknownTableNames)
                        {
                            if (!report.Images.UnknownTableNames.Contains(tableName))
                                report.Images.UnknownTableNames.Add(tableName);
                        }
                    }
                    var parts = suffix.TrimStart('_').Split('_', StringSplitOptions.RemoveEmptyEntries);
                    var blockKey = parts.Length >= 1 ? parts[0] : "0";
                    var idx = parts.Length >= 2 && int.TryParse(parts[1], out var p) ? p : 0;

                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(b64); }
                    catch
                    {
                        report.Warnings.Add($"image payload for subject {s.SubjectId} is not valid base64");
                        continue;
                    }

                    // The service returns image blobs in a power-of-two sized buffer with the
                    // tail zero-filled (e.g. a 129,858 byte PNG comes back as 131,072 bytes).
                    // The Windows client trims that padding before storing; so must we, otherwise
                    // every affected file differs from the Access export byte-for-byte.
                    var trimmed = TrimImagePadding(bytes);
                    if (trimmed.Length != bytes.Length)
                    {
                        report.Images.PaddedFilesTrimmed++;
                        report.Images.PaddedBytesTrimmed += bytes.Length - trimmed.Length;
                        bytes = trimmed;
                    }

                    var name = parts.Length >= 2
                        ? $"{s.SubjectId}_{blockKey}_{idx}.{ext}"
                        : $"{s.SubjectId}_{blockKey}.{ext}";
                    // 文件名片段（blockKey 来自上游 ctblname）可能含路径分隔符：
                    // 落盘前必须确认最终路径仍在该课程的图片目录内，否则跳过（防路径穿越）。
                    var safeDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var targetPath = Path.GetFullPath(Path.Combine(dir, name));
                    if (!targetPath.StartsWith(safeDir, StringComparison.Ordinal))
                    {
                        report.Images.FilesFailed++;
                        if (report.Images.FailedSample.Count < 8)
                        {
                            report.Images.FailedSample.Add($"{name}: 非法文件名，已跳过");
                        }
                        continue;
                    }
                    try
                    {
                        await File.WriteAllBytesAsync(targetPath, bytes, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 单张图写不进去（权限、瞬时 IO 故障、共享目录抖动）不该毁掉整门课：
                        // 记下来、跳过，占位符保持未解析（与"服务端没给这张图"同一种表现）。
                        report.Images.FilesFailed++;
                        if (report.Images.FailedSample.Count < 8)
                        {
                            report.Images.FailedSample.Add($"{name}: {ex.Message}");
                        }
                        continue;
                    }
                    report.Images.FilesWritten++;
                    report.Images.BytesWritten += bytes.Length;

                    if (!blocks.TryGetValue(blockKey, out var map))
                    {
                        map = new Dictionary<int, (string, string)>();
                        blocks[blockKey] = map;
                    }
                    map[idx] = (name, ext);
                }

                updated.Add(ResolvePlaceholders(s, blocks, srcPrefix, report));
            }
            subjects = updated;

            _log($"images: {report.Images.FilesWritten} files ({report.Images.BytesWritten} bytes) from " +
                 $"{report.Images.SubjectsWithImageList}/{report.Images.SubjectsQueried} subjects; " +
                 $"placeholders replaced {report.Images.PlaceholdersReplaced}, " +
                 $"unresolved {report.Images.PlaceholdersUnresolved} in {report.Images.SubjectsWithUnresolved} subjects");
            if (report.Images.FilesFailed > 0)
            {
                report.Warnings.Add($"{report.Images.FilesFailed} 张题图写入失败已跳过（不中断整门课）：" +
                                    string.Join(" | ", report.Images.FailedSample));
            }
            if (report.Images.PlaceholdersUnresolved > 0)
            {
                report.Warnings.Add($"{{图}} placeholder unresolved {report.Images.PlaceholdersUnresolved} times in " +
                                    $"{report.Images.SubjectsWithUnresolved} subjects - no matching image was returned");
            }
            if (report.Images.UnknownTableNames.Count > 0)
            {
                report.Warnings.Add("unexpected ctblname values: " + string.Join(",", report.Images.UnknownTableNames));
            }
            Report("images", targets.Count, targets.Count, $"{report.Images.FilesWritten} 张");
        }

        // ---- 5. 完整性闸（BE-10）：集合不完整/只拉了一部分时，破坏性的整课替换必须被拒绝 ----
        // 判定放在 dry-run 之前算，这样 dry run 也能预告"真跑会不会被闸掉"。
        var incomplete = IncompleteReasons(o, ids, chapters, subjects, missing, report);
        var partialFlags = DescribePartialFetch(o);
        var replaceIsPartial = o.ReplaceCourse && (incomplete.Count > 0 || partialFlags.Count > 0);

        if (o.DryRun)
        {
            CleanupStaging(dryRunStage);
            report.Notes.Add("dry run: 题库与题图目录零写入（题图只落在临时 staging，已删除）");
            if (o.ReplaceCourse && partialFlags.Count > 0 && !o.AllowPartialReplace)
            {
                report.Notes.Add("dry run 预警：真跑会被拒绝（部分拉取 + 整课替换）—— " +
                                 string.Join("、", partialFlags));
            }
            if (o.ReplaceCourse && incomplete.Count > 0 && !o.AllowPartialReplace)
            {
                report.Notes.Add("dry run 预警：真跑会被完整性闸拒绝 —— " + string.Join("；", incomplete));
            }
            return;
        }

        if (o.ReplaceCourse && partialFlags.Count > 0 && !o.AllowPartialReplace)
        {
            throw new InvalidOperationException(
                "拒绝整课替换：这次是**部分拉取**（" + string.Join("、", partialFlags) + "），" +
                "替换会删掉没有下载的章节/题目（incremental 只拿到水位之后变更的行）。" +
                "改用整课语义（--mode full + --subject-source union），或加 --no-replace 只 upsert；" +
                "确认要删旧内容再加 --allow-partial-replace。");
        }
        if (o.ReplaceCourse && incomplete.Count > 0 && !o.AllowPartialReplace)
        {
            throw new InvalidOperationException(
                "拒绝提交残缺全课（旧内容一行未动、水位未推进）：" + string.Join("；", incomplete) + "。" +
                "网络/上游恢复后重试即可；确实要接受这批部分数据，显式加 --allow-partial-replace" +
                "（届时也不会推进水位，下次拉取仍会补齐这些缺口）。");
        }
        if (!o.ReplaceCourse && incomplete.Count > 0)
        {
            report.Warnings.Add("本次集合不完整（" + string.Join("；", incomplete) +
                                "）：只 upsert 已拿到的行，且**不推进水位**（下次拉取会重新拉这些缺口）。");
        }
        if (replaceIsPartial && o.AllowPartialReplace)
        {
            report.Warnings.Add("--allow-partial-replace 已显式打开：" +
                                (partialFlags.Count > 0 ? "部分拉取（" + string.Join("、", partialFlags) + "）" : "残缺集合") +
                                "将照常整课替换，且不推进水位。");
        }
        // 残缺集合不许推进水位：否则下次增量拉取会以为这些缺口已经拿到，缺口永久留下。
        var advanceWatermark = incomplete.Count == 0 && !(o.ReplaceCourse && partialFlags.Count > 0);
        var chapterWatermark = advanceWatermark ? report.Chapters.NewWatermark : null;
        var subjectWatermark = advanceWatermark ? report.Subjects.NewWatermark : null;
        if (!advanceWatermark)
        {
            _log("watermarks: 集合不完整/部分拉取 → 本次不推进水位（dchapterchange/dsubjectchange 保持原值）");
        }

        Report("write", 0, 1, $"{chapters.Count} 章 / {subjects.Count} 题");

        // ---- 6. write sqlite ----
        // dupdatedate: the Access bank stores the client's LOCAL write time here; we store the
        // server's dchangedate instead - it is real upstream data and makes pulls reproducible.
        // Both work: the site only uses it as the decryption key seed (plaintext content is
        // returned as-is when decryption fails) and as the displayed "updated at" value.
        using (var db = new BankWriter(o.OutDb))
        {
            if (!string.IsNullOrEmpty(o.MetaDb)) db.CopyMetadataFrom(o.MetaDb!, o.CourseId);
            // 主库里没有这门课的 course 行（上游有、本地没导入过）→ 从上游课程目录补一行，
            // 否则拉完题也进不了管理端列表。目录本身拉一次缓存在进程里，多门课批量拉时不会重复请求。
            if (!db.HasCourse(o.CourseId))
            {
                var catalogRow = await CourseCatalog.FindAsync(soap, o.CourseId, ct).ConfigureAwait(false);
                if (catalogRow is not null)
                {
                    db.EnsureCourseRow(catalogRow);
                    report.Notes.Add($"课程 {o.CourseId} 元数据不在主库，已按上游目录补写：{catalogRow.Name}");
                    _log($"course row synthesized from upstream catalog: {o.CourseId} = {catalogRow.Name}");
                }
                else
                {
                    report.Warnings.Add($"上游课程目录里没有课程 {o.CourseId}，该课可能不会出现在课程列表里");
                }
            }
            if (o.ReplaceCourse)
            {
                // 单事务替换：delete + insert + 水位一起提交；中途失败整门课回滚，不留"删了没写"的空课。
                // acceptIncomplete：能走到这里说明调用方已显式放行部分集合（且上面已把水位置空）。
                var written = db.ReplaceCourse(o.CourseId, chapters, subjects,
                    chapterWatermark, subjectWatermark, acceptIncomplete: replaceIsPartial);
                report.Chapters.Written = written.Chapters;
                report.Subjects.Written = written.Subjects;
            }
            else
            {
                report.Chapters.Written = db.InsertChapters(chapters);
                report.Subjects.Written = db.InsertSubjects(subjects);
                db.SetCourseWatermarks(o.CourseId, chapterWatermark, subjectWatermark);
            }
            report.Chapters.DbRowsAfter = (int)db.Count("coursechapter", o.CourseId);
            report.Subjects.DbRowsAfter = (int)db.Count("coursesubject", o.CourseId);
        }
        _log($"sqlite: chapter rows after write = {report.Chapters.DbRowsAfter}, " +
             $"subject rows after write = {report.Subjects.DbRowsAfter} ({report.OutDb})");
        Report("write", 1, 1, $"{report.Subjects.Written} 题入库");
    }

    /// <summary>
    /// Replace {图} / {图N} placeholders with the &lt;img&gt; tag the site expects.
    /// Block numbers come from the server (ctblname "coursesubjectN"); each text field owns a
    /// FIXED block - ctitle -> 0, cdescription -> 1. Blocks are not consumed sequentially by
    /// field order: a field keeps its own block even when an earlier field has no placeholders.
    /// Verified against the Access export across all 625 placeholder-bearing subjects of
    /// course 24: this rule reproduces the export's img src list 625/625, while sequential
    /// assignment only reaches 606/625 (it wrongly gives the description block 0 whenever the
    /// title carries no picture, e.g. subject 2970592).
    /// "{图}" -> _N.ext and "{图k}" -> _N_k.ext; a placeholder whose image the server did not
    /// send stays untouched, which is also what the client does (e.g. 2970592 keeps {图2}/{图3}).
    /// </summary>
    /// <summary>
    /// 取一批题面。上游对个别题目会直接 500（数据缺陷，缩小批次也一样），
    /// 所以**只对这种上游 500**做二分：拆到单题仍失败才记进 <c>BadIds</c>。
    ///
    /// 网络故障 / 鉴权失败 / 协议错误 / 取消**绝不**二分、绝不降级成"坏题"：
    /// 二分只会把一次整站故障摊成几百个"坏题"，然后整门课被残缺集合替换掉（BE-10）。
    /// </summary>
    private async Task<List<Dictionary<string, string?>>> FetchSubjectBatchAsync(
        SoapClient soap, long courseId, IReadOnlyList<long> batch, PullReport report, CancellationToken ct)
    {
        var args = new StringBuilder("<courseId>").Append(courseId).Append("</courseId><lstSubjectId>");
        foreach (var id in batch) args.Append("<int>").Append(id).Append("</int>");
        args.Append("</lstSubjectId>");

        try
        {
            var resp = await soap.InvokeAsync("LoadCourseSubjectListInfo", args.ToString(), ct).ConfigureAwait(false);
            return JkdParsers.ParseDataTable(resp.Primary);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消：立即透传
        }
        catch (SoapCallException ex) when (ex.Kind == SoapFailureKind.Upstream && batch.Count > 1)
        {
            var half = batch.Count / 2;
            _log($"  batch of {batch.Count} failed ({ShortError(ex)}); splitting in half");
            var first = await FetchSubjectBatchAsync(soap, courseId, batch.Take(half).ToList(), report, ct).ConfigureAwait(false);
            var second = await FetchSubjectBatchAsync(soap, courseId, batch.Skip(half).ToList(), report, ct).ConfigureAwait(false);
            return first.Concat(second).ToList();
        }
        catch (SoapCallException ex) when (ex.Kind == SoapFailureKind.Upstream)
        {
            // 二分到单题仍是 500：这才是"单题数据缺陷"，可以跳过（是否允许跳过由完整性闸决定）
            report.Subjects.BadIds.Add(batch[0]);
            report.Warnings.Add($"题号 {batch[0]} 上游返回 HTTP 500 数据错误，已跳过（{ShortError(ex)}）");
            _log($"  subject {batch[0]} failed with upstream 500, skipped: {ShortError(ex)}");
            return new List<Dictionary<string, string?>>();
        }
        catch (SoapCallException)
        {
            // 网络 / 鉴权 / 协议：与题目内容无关，必须让整次拉取失败
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // 批返回的内容不是可用 DataTable（契约破了）：同样整次失败，不当坏题
            throw new SoapCallException(SoapFailureKind.Protocol, "LoadCourseSubjectListInfo",
                $"LoadCourseSubjectListInfo 返回的内容不是可用 DataTable：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// "这次拉的是不是完整整课集合"的判定（BE-11）：出现任何一个这类参数，集合就不再是
    /// "整课全量"，拿它做整课替换都会删掉没下载的部分。
    /// </summary>
    public static List<string> DescribePartialFetch(PullOptions o)
    {
        var flags = new List<string>();
        if (o.Mode != "full") flags.Add("--mode " + o.Mode);
        if (o.SubjectSource != "union") flags.Add("--subject-source " + o.SubjectSource);
        if (o.MaxSubjects is not null) flags.Add("--max-subjects");
        if (o.ChapterSince is not null) flags.Add("--chapter-since");
        if (o.SubjectSince is not null) flags.Add("--subject-since");
        return flags;
    }

    /// <summary>集合不完整的具体原因（缺行 / 坏题 / 空列表），用于"拒绝提交"时的说明。</summary>
    private static List<string> IncompleteReasons(PullOptions o, List<long> ids, List<ChapterRow> chapters,
        List<SubjectRow> subjects, List<long> missing, PullReport report)
    {
        var reasons = new List<string>();
        if (missing.Count > 0)
        {
            reasons.Add($"{missing.Count} 个题号上游没有返回数据" +
                        $"(sample: {string.Join(",", report.Subjects.MissingSample)})");
        }
        if (report.Subjects.BadIds.Count > 0)
        {
            reasons.Add($"{report.Subjects.BadIds.Count} 个题号上游持续返回 500" +
                        $"(sample: {string.Join(",", report.Subjects.BadIds.Take(20))})");
        }
        // 空列表只有在"整课全量"语义下才是残缺：部分拉取本来就只拿到水位之后的变更行。
        if (DescribePartialFetch(o).Count == 0)
        {
            if (chapters.Count == 0) reasons.Add("章节列表为空");
            if (ids.Count == 0) reasons.Add("上游题号列表为空");
            else if (subjects.Count == 0) reasons.Add("题目列表为空");
        }
        return reasons;
    }

    /// <summary>
    /// "坏题"容忍上限：单题数据缺陷是少数现象；超过这个量更像上游整体故障，
    /// 直接中止（也避免二分把每一批摊成几百次请求）。
    /// </summary>
    private static int BadIdTolerance(int requestedCount) => Math.Max(20, requestedCount / 50);

    /// <summary>删掉 dry-run 的临时 staging：dry run 的契约是零写入，临时目录也不留。</summary>
    private static void CleanupStaging(string? stagingRoot)
    {
        if (string.IsNullOrEmpty(stagingRoot)) return;
        try
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
        catch (IOException) { /* 尽力而为：删不掉只是临时目录残留 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
    }

    private static string ShortError(Exception ex)
    {
        var text = (ex.Message ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        var cut = text.IndexOf("<soap", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) text = text[..cut].Trim();
        return text.Length > 120 ? text[..120] + "…" : text;
    }

    private static SubjectRow ResolvePlaceholders(
        SubjectRow s,
        SortedDictionary<string, Dictionary<int, (string Name, string Ext)>> blocks,
        string srcPrefix,
        PullReport report)
    {
        var fields = new (string Name, string? Text, string Block)[]
        {
            ("ctitle", s.Title, "0"),
            ("cdescription", s.Description, "1"),
        };
        var withPlaceholders = fields
            .Where(f => f.Text is not null && PlaceholderRegex.IsMatch(f.Text)).ToList();
        if (withPlaceholders.Count == 0) return s;

        var unassigned = new List<string>(blocks.Keys);
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        var anyUnresolved = false;
        var unresolvedCount = 0;
        var replacedCount = 0;

        foreach (var (name, text, block) in withPlaceholders)
        {
            // Preferred block first; if the server sent no such block, fall back to a block
            // that no other field has claimed so unusual payloads still resolve.
            var key = blocks.ContainsKey(block) ? block : unassigned.FirstOrDefault();
            if (key is not null) unassigned.Remove(key);
            var map = key is null ? null : blocks[key];

            var fieldUnresolved = 0;
            var newText = PlaceholderRegex.Replace(text!, m =>
            {
                var raw = m.Groups[1].Value;
                var idx = raw.Length == 0 ? 0 : int.Parse(raw, CultureInfo.InvariantCulture);
                if (map is null || !map.TryGetValue(idx, out var file))
                {
                    fieldUnresolved++;
                    return m.Value;
                }
                replacedCount++;
                return $"<img border=\"0\" alt=\"\" src=\"{srcPrefix}{file.Name}\">";
            });
            if (fieldUnresolved > 0)
            {
                unresolvedCount += fieldUnresolved;
                anyUnresolved = true;
            }
            resolved[name] = newText;
        }

        report.Images.PlaceholdersReplaced += replacedCount;
        report.Images.PlaceholdersUnresolved += unresolvedCount;
        if (anyUnresolved) report.Images.SubjectsWithUnresolved++;

        return s with
        {
            Title = resolved.GetValueOrDefault("ctitle", s.Title),
            Question = resolved.GetValueOrDefault("cquestion", s.Question),
            Answer = resolved.GetValueOrDefault("canswer", s.Answer),
            Description = resolved.GetValueOrDefault("cdescription", s.Description),
        };
    }

    /// <summary>
    /// The image service hands back blobs inside a power-of-two buffer whose tail is zero-filled
    /// (a 129,858 byte PNG arrives as 131,072 bytes; a 264,811 byte one as 524,288). Left as-is
    /// the file still renders in most viewers, but it differs from the Access export
    /// byte-for-byte. PNG payloads are cut right after the IEND chunk (the authoritative end of
    /// the stream); anything else gets trailing zero bytes stripped.
    /// </summary>
    /// <summary>
    /// 上游给的图片扩展名（cextname）直接拼进文件名会路径穿越：带 "/" 就能写到课程目录之外。
    /// 只接受 1~8 位字母数字，否则退回 png。
    /// </summary>
    private static string SanitizeImageExt(string? raw)
    {
        var ext = (raw ?? "").Trim().TrimStart('.');
        if (ext.Length is > 0 and <= 8 && ext.All(char.IsLetterOrDigit)) return ext.ToLowerInvariant();
        return "png";
    }

    private static byte[] TrimImagePadding(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            var p = 8;
            while (p + 8 <= b.Length)
            {
                var len = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
                if (b[p + 4] == 0 && b[p + 5] == 0 && b[p + 6] == 0 && b[p + 7] == 0) break; // hit the padding
                if (len < 0 || (long)p + 12 + len > b.Length) break;
                var isIend = b[p + 4] == (byte)'I' && b[p + 5] == (byte)'E' &&
                             b[p + 6] == (byte)'N' && b[p + 7] == (byte)'D';
                p += 12 + len;
                if (isIend) return p == b.Length ? b : b[..p];
            }
        }
        var last = b.Length;
        while (last > 0 && b[last - 1] == 0) last--;
        return last == b.Length ? b : b[..last];
    }

    private static bool IsEmptyContent(SubjectRow r) =>
        string.IsNullOrEmpty(r.Question) && string.IsNullOrEmpty(r.Answer) && string.IsNullOrEmpty(r.Description);

    private static ChapterRow MapChapter(Dictionary<string, string?> r, long courseId, PullReport report)
    {
        var id = JkdParsers.ToInt(r.GetValueOrDefault("ichapterid"));
        if (id is null) throw new InvalidOperationException("chapter row without ichapterid");
        var cid = JkdParsers.ToInt(r.GetValueOrDefault("icourseid")) ?? courseId;
        if (cid != courseId) report.Warnings.Add($"chapter {id} reports icourseid={cid}");
        return new ChapterRow(
            id.Value,
            cid,
            r.GetValueOrDefault("cchaptername"),
            r.GetValueOrDefault("cchaptercode"),
            JkdParsers.ToInt(r.GetValueOrDefault("igrade")),
            JkdParsers.ToInt(r.GetValueOrDefault("itype")),
            JkdParsers.ToInt(r.GetValueOrDefault("icount")),
            JkdParsers.ToBoolInt(r.GetValueOrDefault("bstopflag")));
    }

    private static SubjectRow? MapSubject(Dictionary<string, string?> r, long courseId, PullReport report)
    {
        var id = JkdParsers.ToInt(r.GetValueOrDefault("isubjectid"));
        if (id is null) { report.Warnings.Add("subject row without isubjectid"); return null; }
        return new SubjectRow(
            id.Value,
            JkdParsers.ToInt(r.GetValueOrDefault("icourseid")) ?? courseId,
            JkdParsers.ToInt(r.GetValueOrDefault("ichapterid")) ?? 0,
            JkdParsers.ToInt(r.GetValueOrDefault("isubjecttype")),
            JkdParsers.ToInt(r.GetValueOrDefault("ichaptertype")),
            JkdParsers.ToInt(r.GetValueOrDefault("iindex")),
            JkdParsers.NormalizeScore(r.GetValueOrDefault("iscore")),
            r.GetValueOrDefault("ctitle"),
            r.GetValueOrDefault("cquestion"),
            r.GetValueOrDefault("canswer"),
            JkdParsers.ToInt(r.GetValueOrDefault("ianswercount")),
            r.GetValueOrDefault("cdescription"),
            JkdParsers.ToDbDate(r.GetValueOrDefault("dchangedate")),
            JkdParsers.ToBoolInt(r.GetValueOrDefault("bstopflag")),
            JkdParsers.ToInt(r.GetValueOrDefault("brich")) ?? 0)
        {
            HasPictureFlag = JkdParsers.ToBool(r.GetValueOrDefault("bpicquestion")) ||
                             JkdParsers.ToBool(r.GetValueOrDefault("bpicanswer")),
        };
    }

    private static string? ReadWatermark(PullOptions o, string column)
    {
        var path = o.MetaDb ?? o.OutDb;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var cn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(path),
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT {column} FROM course WHERE icourseid = $c";
            cmd.Parameters.AddWithValue("$c", o.CourseId);
            var v = cmd.ExecuteScalar() as string;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch
        {
            return null;
        }
    }

    public static string ToJson(PullReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
}
