namespace JkdWeb.BankPuller;

/// <summary>Flattened view of a pull, shaped for API responses.</summary>
public sealed record PullSummary(
    long CourseId,
    int Chapters,
    int Subjects,
    int Images,
    int ImagesFailed,
    int AdSubjectsCleaned,
    int AdHits,
    int AdCharsRemoved,
    int SoapCalls,
    double ElapsedSeconds)
{
    public static PullSummary From(PullReport report) => new(
        report.CourseId,
        report.Chapters.Written,
        report.Subjects.Written,
        report.Images.FilesWritten,
        report.Images.FilesFailed,
        report.Ads.SubjectsCleaned,
        report.Ads.AdHits,
        report.Ads.CharsRemoved,
        report.SoapCalls,
        report.ElapsedSeconds);

    public string Describe() =>
        $"题库已更新：{Chapters} 章 / {Subjects} 题 / {Images} 张图"
        + (AdHits > 0 ? $"，并清洗广告 {AdHits} 处" : "")
        + (ImagesFailed > 0 ? $"，{ImagesFailed} 张图写入失败已跳过" : "");
}

/// <summary>
/// Entry point shared by both admin front-ends (the Windows server and the Docker image),
/// so the pull semantics stay in one place.
/// </summary>
public static class BankPull
{
    /// <summary>
    /// Options for pulling one course into a bank with the site's defaults: full pull,
    /// union of both id lists, only subjects flagged as having images, ads stripped.
    /// <paramref name="metaDb"/> supplies the course row and the lookup tables when the target
    /// is NOT the live bank (staging); pass null when pulling in place, because SQLite refuses
    /// to ATTACH a database file to itself.
    /// </summary>
    public static PullOptions CourseOptions(
        long courseId,
        string outDb,
        string assetsRoot,
        string? metaDb,
        string? serviceUrl = null,
        string? user = null,
        string? password = null) => new()
        {
            CourseId = courseId,
            OutDb = outDb,
            AssetsRoot = assetsRoot,
            MetaDb = string.IsNullOrWhiteSpace(metaDb) ? null : metaDb,
            ServiceUrl = BankCredentials.ResolveUrl(serviceUrl),
            User = BankCredentials.ResolveUser(user),
            Password = BankCredentials.ResolvePassword(password),
            Mode = "full",
            SubjectSource = "union",
            ImageMode = "flagged",
            CleanAds = true,
            ReplaceCourse = true,
        };

    /// <summary>Runs a pull and throws when it did not succeed.</summary>
    public static async Task<PullSummary> PullCourseAsync(
        PullOptions options,
        Action<string>? log = null,
        Action<PullProgress>? progress = null,
        CancellationToken ct = default)
    {
        var engine = new PullEngine(log, progress);
        var report = await engine.RunAsync(options, ct).ConfigureAwait(false);
        if (!report.Ok) throw new InvalidOperationException(report.Error ?? "题库拉取失败");
        return PullSummary.From(report);
    }
}
