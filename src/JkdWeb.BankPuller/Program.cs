using System.Globalization;
using JkdWeb.BankPuller;

// ---------------------------------------------------------------------------
// JkdWeb.BankPuller - cross-platform JinKaodian question-bank puller (PoC)
//
// Pulls course content straight from the ExamClientService SOAP endpoint into a
// site-compatible SQLite bank + assets directory. Replaces the PowerShell path
// (scripts/pull-bank-update.ps1) which needs Windows + Jet OLEDB + the client DLLs.
// ---------------------------------------------------------------------------

var opts = new PullOptions();
string? reportPath = null;
var quiet = false;
var cleanOnly = false;
string? backupDir = null;

for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    string Next(string name) => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} needs a value");

    switch (a)
    {
        case "--course": opts.CourseId = long.Parse(Next(a), CultureInfo.InvariantCulture); break;
        case "--out-db": opts.OutDb = Next(a); break;
        case "--assets": opts.AssetsRoot = Next(a); break;
        case "--meta-db": opts.MetaDb = Next(a); break;
        case "--service-url": opts.ServiceUrl = Next(a); break;
        case "--user": opts.User = Next(a); break;
        case "--password": opts.Password = Next(a); break;
        case "--mode": opts.Mode = Next(a).ToLowerInvariant(); break;
        case "--subject-source": opts.SubjectSource = Next(a).ToLowerInvariant(); break;
        case "--chapter-since": opts.ChapterSince = Next(a); break;
        case "--subject-since": opts.SubjectSince = Next(a); break;
        case "--full-since": opts.FullSince = Next(a); break;
        case "--batch": opts.BatchSize = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
        case "--images": opts.FetchImages = ParseBool(Next(a)); break;
        case "--image-mode": opts.ImageMode = Next(a).ToLowerInvariant(); break;
        case "--skip-empty": opts.SkipEmpty = true; break;
        case "--clean-ads": opts.CleanAds = ParseBool(Next(a)); break;
        case "--no-clean-ads": opts.CleanAds = false; break;
        case "--clean-ads-only": cleanOnly = true; break;
        case "--backup-dir": backupDir = Next(a); break;
        case "--max-subjects": opts.MaxSubjects = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
        case "--no-replace": opts.ReplaceCourse = false; break;
        case "--allow-partial-replace": opts.AllowPartialReplace = true; break;
        case "--dry-run": opts.DryRun = true; break;
        case "--report": reportPath = Next(a); break;
        case "--quiet": quiet = true; break;
        case "-h" or "--help":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument: {a}");
            PrintUsage();
            return 2;
    }
}

// ---------------------------------------------------------------------------
// CLI 语义整理（BE-11）：默认值不许暗藏破坏性操作
// ---------------------------------------------------------------------------
if (opts.Mode is not ("full" or "incremental"))
{
    Console.Error.WriteLine($"[puller] --mode 只支持 full|incremental（收到 '{opts.Mode}'）");
    return 2;
}

// incremental 只拿到"水位之后变更"的章节/题目：再去做整课替换，等于删掉没变的那些
// （尤其 --subject-source group 时只有增量题目）。所以增量模式固定 upsert，不删旧内容。
if (opts.Mode == "incremental")
{
    if (!opts.AllowPartialReplace)
    {
        opts.ReplaceCourse = false;
        Console.WriteLine("[puller] incremental：只 upsert 水位之后的变更行，不删除既有章节/题目" +
                          "（要整课替换请用 --mode full）");
    }
    else
    {
        Console.WriteLine("[puller] 警告：incremental + --allow-partial-replace → 增量集合会整课替换（删掉未变更部分）");
    }
}

if (!cleanOnly)
{
    // 调试/部分拉取参数默认只能 staging/dry-run：与"整课替换"同时出现时提前挡住（exit 2 = 用法错误）。
    // 语义层（PullEngine）还有同一道闸，这里只是让错误更早、更明确。
    var partialFlags = PullEngine.DescribePartialFetch(opts);
    if (partialFlags.Count > 0 && opts.ReplaceCourse && !opts.DryRun && !opts.AllowPartialReplace)
    {
        Console.Error.WriteLine(
            "[puller] 拒绝执行：" + string.Join("、", partialFlags) +
            " 是部分拉取，和整课替换（会删掉没下载的章节/题目）组合不安全。\n" +
            "          加 --dry-run（只看不写）、--no-replace（只 upsert），" +
            "或确认要删旧内容再加 --allow-partial-replace。");
        return 2;
    }
}

if (cleanOnly)
{
    if (string.IsNullOrEmpty(opts.OutDb))
    {
        Console.Error.WriteLine("--clean-ads-only needs --out-db <bank file>");
        PrintUsage();
        return 2;
    }
    var result = AdCleaner.CleanDatabase(opts.OutDb, opts.CourseId, backupDir, quiet ? null : Console.WriteLine);
    Console.WriteLine($"[puller] clean-ads-only ok courses={result.Courses} scanned={result.SubjectsScanned} " +
                      $"cleaned={result.SubjectsCleaned} ads={result.AdHits} charsRemoved={result.CharsRemoved} " +
                      $"backup={result.BackupPath ?? "-"}");
    return 0;
}

if (opts.CourseId <= 0 || string.IsNullOrEmpty(opts.OutDb))
{
    PrintUsage();
    return 2;
}
if (string.IsNullOrEmpty(opts.AssetsRoot))
{
    opts.AssetsRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.OutDb)) ?? ".", "assets");
}

// 账号不内置在代码里：没给 --user/--password 就从环境变量取（JKD_BANK_USER / JKD_BANK_PASSWORD）
opts.ServiceUrl = BankCredentials.ResolveUrl(opts.ServiceUrl);
opts.User = BankCredentials.ResolveUser(opts.User);
opts.Password = BankCredentials.ResolvePassword(opts.Password);
try
{
    BankCredentials.EnsureConfigured(opts.User, opts.Password);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"[puller] {ex.Message}");
    return 2;
}

var engine = new PullEngine(quiet ? null : Console.WriteLine);
Console.WriteLine($"[puller] course={opts.CourseId} mode={opts.Mode} source={opts.SubjectSource} " +
                  $"replace={(opts.ReplaceCourse ? "yes" : "no")} dryRun={(opts.DryRun ? "yes" : "no")} " +
                  $"db={opts.OutDb} assets={opts.AssetsRoot} " +
                  $"service={BankCredentials.Describe(opts.ServiceUrl, opts.User, opts.Password)}");

var report = await engine.RunAsync(opts);

// dry-run 的契约是"零写入"：没显式给 --report 就不落盘（也不创建目录），报告打到 stdout。
if (reportPath is null && !opts.DryRun)
{
    reportPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.OutDb)) ?? ".", "pull-report.json");
}
if (reportPath is null)
{
    Console.WriteLine("[puller] dry-run: 未指定 --report → 报告不落盘（nothing written）");
    if (!quiet) Console.WriteLine(PullEngine.ToJson(report));
}
else
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, PullEngine.ToJson(report));
        Console.WriteLine($"[puller] report written to {reportPath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[puller] could not write report: {ex.Message}");
    }
}

Console.WriteLine($"[puller] ok={report.Ok} chapters={report.Chapters.Written} " +
                  $"subjects={report.Subjects.Written} images={report.Images.FilesWritten} " +
                  $"imagesFailed={report.Images.FilesFailed} " +
                  $"soapCalls={report.SoapCalls} elapsed={report.ElapsedSeconds}s");
if (!report.Ok) Console.Error.WriteLine($"[puller] FAILED: {report.Error}");
return report.Ok ? 0 : 1;

static bool ParseBool(string s) =>
    s.Equals("1", StringComparison.OrdinalIgnoreCase) ||
    s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
    s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
    s.Equals("on", StringComparison.OrdinalIgnoreCase);

static void PrintUsage()
{
    Console.WriteLine("""
        JkdWeb.BankPuller - pull JinKaodian course content into SQLite + assets (cross-platform)

        usage: JkdWeb.BankPuller --course <id> --out-db <file> [options]

          --course <id>            course id to pull (required)
          --out-db <file>          target SQLite bank, created if missing (required)
          --assets <dir>           asset root; images go to <dir>/<courseId>/ (default: <out-db dir>/assets)
          --meta-db <file>         copy course/courseclass/coursesubclass/coursesubjecttype from this bank
          --mode <full|incremental>            full = 全量（first-download sentinel）+ 整课替换（默认）
                                   incremental = 只拉水位之后的变更行，并且只 upsert **不删除**
          --subject-source <union|group|full>  subject id source; union = group + full list (default)
          --chapter-since <date>   override chapter watermark, e.g. "2026-06-16 16:24:26.999"
          --subject-since <date>   override subject watermark
          --full-since <date>      sentinel used for full mode (default 1900-01-01 00:00:00.999)
          --batch <n>              subject download batch size (default 200)
          --images <true|false>    fetch subject images (default true)
          --image-mode <flagged|all|none>      flagged = only subjects flagged as having images (default)
          --skip-empty             drop rows whose question/answer/description are all empty
          --clean-ads <true|false> strip upstream promotional text from descriptions (default true)
          --no-clean-ads           same as --clean-ads false
          --clean-ads-only         do not pull; only clean an existing bank in place (needs --out-db)
          --backup-dir <dir>       with --clean-ads-only: copy the bank here before writing
          --max-subjects <n>       cap number of subjects; 调试参数 —— 需配 --dry-run / --no-replace
          --no-replace             keep existing rows of this course instead of replacing（只 upsert）
          --allow-partial-replace  显式接受"部分/残缺集合也整课替换"（默认拒绝提交残缺全课；
                                   放行时也不推进水位，下次拉取仍会补齐缺口）
          --dry-run                query only：题库与题图目录零写入（题图只写临时 staging 且删掉）
          --report <file>          report json path (default <out-db dir>/pull-report.json;
                                   dry-run 未指定时不落盘，报告打到 stdout)
          --quiet                  suppress progress output

        upstream account (not built into the code; pick one):
          --user <name>            service account; falls back to env JKD_BANK_USER
          --password <pwd>         service password; falls back to env JKD_BANK_PASSWORD
          --service-url <url>      override service url; falls back to env JKD_BANK_URL
                                   (default: the official JinKaodian ExamClientService url)

        Docker example (keeps the password out of `ps`):
          docker run --env-file data/bank-credentials.env ...
        """);
}
