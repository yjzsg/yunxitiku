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
                  $"db={opts.OutDb} assets={opts.AssetsRoot} " +
                  $"service={BankCredentials.Describe(opts.ServiceUrl, opts.User, opts.Password)}");

var report = await engine.RunAsync(opts);

reportPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.OutDb)) ?? ".", "pull-report.json");
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
          --mode <full|incremental>            full = first-download sentinel (default), incremental = since stored watermark
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
          --max-subjects <n>       cap number of subjects (debug)
          --no-replace             keep existing rows of this course instead of replacing
          --dry-run                query only, write nothing
          --report <file>          report json path (default <out-db dir>/pull-report.json)
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
