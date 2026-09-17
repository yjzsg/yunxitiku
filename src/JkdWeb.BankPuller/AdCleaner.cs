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
/// Observed wordings (the domain is sometimes misspelled and "压卷" is sometimes "Y卷",
/// so the patterns tolerate that):
///   新版章节练习，考前压卷，完整优质题库+考生笔记分享，实时更新，金考典软件，下载链接 www.jinkaodian.com，
///   新版习题，考前押题，金考典软件考前更新，更多考生做题笔记分享，下载链接 www.jinkaodian.com
///   考前【黑钻押题】，金考典软件考前一周更新，下载链接 www.jinkaodian.com
///   【彩蛋】压卷，金考典软件考前更新，下载链接 www.jinkaodian.com
///   (jinkdoain.com 金考典题库)
///
/// IMPORTANT: "jinkaodian" also appears ~4,000 times inside &lt;img src="C:\...\jinkaodian\data\..."&gt;
/// image paths. Matching the bare token would destroy every image reference, so the rules
/// only ever match the promo wordings themselves (they all contain 金考典软件/金考典题库).
/// </summary>
public static class AdCleaner
{
    private const string Url = @"(?:w{2,3}\.)?jink[a-z]*\.com";

    private static readonly Regex[] AdPatterns =
    {
        // 新版章节练习/习题，…压卷|Y卷，…金考典软件(，|考前更新，)…下载链接 <url>
        new(@"新版(?:章节练习|习题)，[^，。\n]{0,16}，(?:完整优质题库\+考生笔记分享，实时更新，)?金考典软件(?:，|考前[^，。\n]{0,10}，)(?:更多考生做题笔记分享，)?下载链接\s*" + Url + @"，?[ \t]*",
            RegexOptions.Compiled),
        // 考前【黑钻押题】/【彩蛋】…，金考典软件考前(一周)?更新，下载链接 <url>
        new(@"(?:考前【黑钻押题】|【彩蛋】[^，。\n]{0,10})，金考典软件考前(?:一周)?更新，下载链接\s*" + Url + @"[ \t]*",
            RegexOptions.Compiled),
        // safety net: any bare "下载链接 <url>"
        new(@"下载链接\s*" + Url + @"，?[ \t]*", RegexOptions.Compiled),
        // (jinkdoain.com 金考典题库)
        new(@"\(\s*" + Url + @"\s+金考典题库\s*\)", RegexOptions.Compiled),
    };

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
        foreach (var rx in AdPatterns)
        {
            var matches = rx.Matches(result);
            if (matches.Count == 0) continue;
            hits += matches.Count;
            result = rx.Replace(result, "");
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
