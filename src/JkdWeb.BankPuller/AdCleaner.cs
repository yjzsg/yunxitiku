using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace JkdWeb.BankPuller;

/// <summary>Outcome of cleaning one bank (or one course of it).</summary>
public sealed record AdCleanResult(
    int Courses,
    int SubjectsScanned,
    int SubjectsCleaned,
    int AdHits,
    int CharsRemoved,
    string? BackupPath);

/// <summary>
/// Strips the promotional text the upstream JinKaodian service splices into subject
/// descriptions. Verified against the live bank (29,738 subjects / 1,129 courses):
/// 47 subjects carried ads across 5 courses (20, 21, 23, 24, 570), all in cdescription.
///
/// Observed wordings (the domain is sometimes misspelled, "压卷" is sometimes "Y卷",
/// and lately the mark itself is written with spaces/commas inside):
///   新版章节练习，考前压卷，完整优质题库+考生笔记分享，实时更新，金考典软件，下载链接 www.jinkaodian.com，
///   新版习题，考前押题，金考典软件考前更新，更多考生做题笔记分享，下载链接 www.jinkaodian.com
///   考前【黑钻押题】，金考典软件考前一周更新，下载链接 www.jinkaodian.com
///   【彩蛋】压卷，金考典软件考前更新，下载链接 www.jinkaodian.com
///   (jinkdoain.com 金考典题库)
///   （新版章节练习，考前压卷，考生笔记分享，实时更新，金,考 典 APP，ww.ji nkaod ian.com）
///
/// ⚠️ 2026-10 的线上残留（18 条）**没有 URL**：上一次清洗把「下载链接 <url>」整段删掉了，
/// 而老规则要求结尾必须有那个 URL，于是「…金考典软件考前更新，」「金考典软件，」这些
/// 剩下的半截促销句再也匹配不上、永久留在库里。所以现在的规则**不依赖 URL**：
///   1. 先看「促销段」——促销词（新版章节练习/考前压卷/考生笔记分享/实时更新/下载链接/域名…）
///      被「，,、」和空格串起来的一整段；
///   2. 段里出现**锚点**（金考典 / jink / 下载链接 / 黑钻押题）才删整段 ——
///      单独一个「实时更新」是正文里的正常词，没有锚点就一个字符都不动；
///   3. 促销段可能被**插在正文中间**（实测「如住宅楼的客厅、金考典软件，卧室、厨房…」），
///      删整段就是精确删掉插进去的那一小截，不动周围正文，绝不按「删整行/整段」处理。
///
/// IMPORTANT: "jinkaodian" also appears ~4,000 times inside &lt;img src="C:\...\jinkaodian\data\..."&gt;
/// image paths. Matching the bare token would destroy every image reference, so the anchor
/// is the Chinese mark 金考典 (tolerating "金,考 典") plus the *full domain* — never the bare
/// "jinkaodian" path token (which has no ".com").
/// </summary>
public static class AdCleaner
{
    /* 域名：老样式 www.jinkaodian.com；线上还见到把空格塞进字母里的防清洗写法
       （ww.ji nkaod ian.com）。字母之间只容忍空格/制表符，不容许跳行。 */
    private const string Url = @"(?:w{1,3}[ \t]*\.[ \t]*)?j[ \t]*i[ \t]*n[ \t]*k[a-z][a-z \t]{0,10}\.[ \t]*c[ \t]*o[ \t]*m";

    /* 促销句的零件。单独拿出来都不足以判定是广告（"实时更新" 正文里也合法），
       它们的用处是拼出「促销段」；删不删由段里有没有锚点决定。 */
    private static readonly string[] PromoParts =
    {
        @"新版(?:章节练习|习题)",
        // 顺序有意义：`考前黑钻押题` 必须排在 `黑钻押题` 前面，否则正则会从「黑钻」开始吃，
        // 把前面的「考前」留在库里（实测 17327964 会留下「考前考前，」）。
        @"考前(?:压卷|Y卷|押题|黑钻押题)",
        @"(?:考前)?【(?:黑钻押题|彩蛋)】[^，。；、\n]{0,12}",
        @"黑钻押题",
        @"(?:完整|更多)?(?:优质)?题库\+考生笔记分享",
        @"(?:更多)?考生做题笔记分享",
        @"考生笔记分享",
        @"实时更新",
        @"下载链接",
        @"金考典题库",
        // 促销标记本体：金考典软件 / 金考典软件考前(一周)更新 / 用金考典软件考 / 金,考 典 APP
        // 顺序有意义：`考前(一周)更新` 必须排在 `考` 前面，否则只吃掉「…软件考」，
        // 把「前更新，」留在库里（实测 17327964 会留下「前更新，」）。
        @"(?:用)?金[ \t]*[,，、]?[ \t]*考[ \t]*[,，、]?[ \t]*典[ \t]*(?:APP|软件)(?:考前(?:一周)?更新|考)?",
        Url,
    };

    /* 一整段促销：促销零件被「，,、」和空格串起来（可以只有一个零件）。 */
    private static readonly Regex PromoRun = new(
        @"(?:(" + string.Join("|", PromoParts) + @")[，,、 \t]*)+",
        RegexOptions.Compiled);

    /* 锚点：段里有这些词才删（金,考 典 这种写法由 金…考…典 覆盖）。 */
    private static readonly Regex PromoAnchor = new(
        @"金[ \t]*[,，、]?[ \t]*考[ \t]*[,，、]?[ \t]*典|j[ \t]*i[ \t]*n[ \t]*k|下载链接|黑钻押题",
        RegexOptions.Compiled);

    /* 整块括号促销（含防清洗写法）：
       （新版章节练习，考前压卷，考生笔记分享，实时更新，金,考 典 APP，ww.ji nkaod ian.com）
       以「新版章节练习/习题，」开头、整段在同一个括号里的，只可能是促销，连括号一起删。 */
    private static readonly Regex ParenPromo = new(
        @"[（(][ \t]*新版(?:章节练习|习题)[，,][^）)\n]{0,240}[）)][ \t]*",
        RegexOptions.Compiled);

    // (jinkdoain.com 金考典题库) —— 老样式，括号里的裸域名 + 题库字样
    private static readonly Regex ParenDomain = new(
        @"[（(][ \t]*" + Url + @"[ \t]+金考典题库[ \t]*[）)][ \t]*",
        RegexOptions.Compiled);

    private static readonly Regex[] BlockPatterns = { ParenPromo, ParenDomain };

    // Cosmetic tidy-up, applied ONLY to text an ad was actually removed from, so the blast
    // radius stays exactly at the ad-affected rows.
    private static readonly (Regex Pattern, string Replacement)[] Normalisations =
    {
        (new Regex(@"[ \t]+(?=\r?\n)", RegexOptions.Compiled), ""),
        (new Regex(@"(?:\r?\n){3,}", RegexOptions.Compiled), "\r\n"),
        (new Regex(@"([，。；])\1+", RegexOptions.Compiled), "$1"),
    };

    /// <summary>Removes ad segments from one field. Returns the cleaned text and how many ads matched.</summary>
    public static (string? Text, int Hits) Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (text, 0);

        var hits = 0;
        var result = text;

        // 1) 括号整块（含「金,考 典 APP」这类防清洗写法）
        foreach (var rx in BlockPatterns)
        {
            var matches = rx.Matches(result);
            if (matches.Count == 0) continue;
            hits += matches.Count;
            result = rx.Replace(result, "");
        }

        // 2) 促销段：段落里出现锚点才整段删；没有锚点的片段原样保留
        //    （正文里的「实时更新」「考生笔记分享」永远不会被单独删掉）。
        var runs = PromoRun.Matches(result);
        if (runs.Count > 0)
        {
            var builder = new StringBuilder(result.Length);
            var cursor = 0;
            var removed = 0;
            foreach (Match run in runs)
            {
                builder.Append(result, cursor, run.Index - cursor);
                cursor = run.Index + run.Length;
                if (PromoAnchor.IsMatch(run.Value)) removed++;
                else builder.Append(result, run.Index, run.Length);
            }
            builder.Append(result, cursor, result.Length - cursor);
            if (removed > 0)
            {
                hits += removed;
                result = builder.ToString();
            }
        }

        if (hits == 0) return (text, 0);

        foreach (var (pattern, replacement) in Normalisations) result = pattern.Replace(result, replacement);
        return (result, hits);
    }

    /// <summary>Cleans the four text fields of a row, preserving every other column.</summary>
    public static (SubjectRow Row, int Hits, int Removed) CleanRow(SubjectRow row)
    {
        var title = Clean(row.Title);
        var question = Clean(row.Question);
        var answer = Clean(row.Answer);
        var description = Clean(row.Description);

        var hits = title.Hits + question.Hits + answer.Hits + description.Hits;
        if (hits == 0) return (row, 0, 0);

        var removed = (row.Title?.Length ?? 0) - (title.Text?.Length ?? 0)
                    + (row.Question?.Length ?? 0) - (question.Text?.Length ?? 0)
                    + (row.Answer?.Length ?? 0) - (answer.Text?.Length ?? 0)
                    + (row.Description?.Length ?? 0) - (description.Text?.Length ?? 0);

        return (row with
        {
            Title = title.Text,
            Question = question.Text,
            Answer = answer.Text,
            Description = description.Text,
        }, hits, removed);
    }

    /// <summary>
    /// Cleans an existing bank in place. Pass courseId 0 to clean every course.
    /// When backupDir is given the database file is copied there before anything is written.
    /// </summary>
    public static AdCleanResult CleanDatabase(string dbPath, long courseId, string? backupDir = null, Action<string>? log = null)
    {
        var full = Path.GetFullPath(dbPath);
        if (!File.Exists(full)) throw new FileNotFoundException("题库文件不存在", full);

        string? backupPath = null;
        if (!string.IsNullOrWhiteSpace(backupDir))
        {
            Directory.CreateDirectory(backupDir);
            backupPath = Path.Combine(backupDir, Path.GetFileName(full));
            File.Copy(full, backupPath, true);
            log?.Invoke($"backup: {backupPath}");
        }

        SqliteConnection.ClearAllPools();
        using var cn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full }.ToString());
        cn.Open();

        var filter = courseId > 0 ? " WHERE icourseid = $c" : "";
        var courses = new HashSet<long>();
        var scanned = 0;
        var cleaned = 0;
        var hits = 0;
        var removed = 0;
        var updates = new List<(long Id, string? Title, string? Question, string? Answer, string? Description)>();

        using (var read = cn.CreateCommand())
        {
            read.CommandText =
                "SELECT isubjectid, icourseid, ctitle, cquestion, canswer, cdescription FROM coursesubject" + filter;
            if (courseId > 0) read.Parameters.AddWithValue("$c", courseId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                scanned++;
                var sid = reader.GetInt64(0);
                var cid = reader.GetInt64(1);
                var title = reader.IsDBNull(2) ? null : reader.GetString(2);
                var question = reader.IsDBNull(3) ? null : reader.GetString(3);
                var answer = reader.IsDBNull(4) ? null : reader.GetString(4);
                var description = reader.IsDBNull(5) ? null : reader.GetString(5);

                var t = Clean(title);
                var q = Clean(question);
                var a = Clean(answer);
                var d = Clean(description);
                var rowHits = t.Hits + q.Hits + a.Hits + d.Hits;
                if (rowHits == 0) continue;

                courses.Add(cid);
                cleaned++;
                hits += rowHits;
                removed += (title?.Length ?? 0) - (t.Text?.Length ?? 0)
                         + (question?.Length ?? 0) - (q.Text?.Length ?? 0)
                         + (answer?.Length ?? 0) - (a.Text?.Length ?? 0)
                         + (description?.Length ?? 0) - (d.Text?.Length ?? 0);
                updates.Add((sid, t.Text, q.Text, a.Text, d.Text));
            }
        }

        if (updates.Count > 0)
        {
            using var tx = cn.BeginTransaction();
            using var write = cn.CreateCommand();
            write.Transaction = tx;
            write.CommandText =
                "UPDATE coursesubject SET ctitle=$t, cquestion=$q, canswer=$a, cdescription=$d WHERE isubjectid=$id";
            var pT = write.Parameters.Add("$t", SqliteType.Text);
            var pQ = write.Parameters.Add("$q", SqliteType.Text);
            var pA = write.Parameters.Add("$a", SqliteType.Text);
            var pD = write.Parameters.Add("$d", SqliteType.Text);
            var pId = write.Parameters.Add("$id", SqliteType.Integer);
            foreach (var u in updates)
            {
                pT.Value = (object?)u.Title ?? DBNull.Value;
                pQ.Value = (object?)u.Question ?? DBNull.Value;
                pA.Value = (object?)u.Answer ?? DBNull.Value;
                pD.Value = (object?)u.Description ?? DBNull.Value;
                pId.Value = u.Id;
                write.ExecuteNonQuery();
            }
            tx.Commit();
        }

        log?.Invoke($"clean-ads: scanned {scanned}, cleaned {cleaned} subjects in {courses.Count} courses, " +
                    $"{hits} ads, {removed} chars removed");
        return new AdCleanResult(courses.Count, scanned, cleaned, hits, removed, backupPath);
    }
}
