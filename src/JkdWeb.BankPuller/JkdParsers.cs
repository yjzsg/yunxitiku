using System.Globalization;
using System.Xml.Linq;

namespace JkdWeb.BankPuller;

/// <summary>One pipe-format subject group: start,end,chapterid,type,iindex,score,anscount.</summary>
public readonly record struct SubjectGroup(
    int Start, int End, int ChapterId, int SubjectType, int Index, string Score, int AnswerCount)
{
    public int Count => End - Start + 1;

    /// <summary>Expand a consecutive id range into individual (subjectId, iindex) pairs.</summary>
    public IEnumerable<(int SubjectId, int Index)> Expand()
    {
        for (var id = Start; id <= End; id++)
        {
            yield return (id, Index + (id - Start));
        }
    }
}

/// <summary>Parsers for the three payload shapes the JinKaodian ASMX service returns.</summary>
public static class JkdParsers
{
    /// <summary>
    /// Parse the escaped .NET DataTable XML carried inside &lt;Op&gt;Result&gt;.
    /// Shape: &lt;DataTable&gt;&lt;xs:schema/&gt;&lt;diffgr:diffgram&gt;&lt;DocumentElement&gt;&lt;tbl&gt;col&lt;/tbl&gt;...
    /// </summary>
    public static List<Dictionary<string, string?>> ParseDataTable(string? xml)
    {
        var rows = new List<Dictionary<string, string?>>();
        if (string.IsNullOrWhiteSpace(xml)) return rows;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("DataTable payload is not valid XML: " + ex.Message);
        }

        foreach (var tbl in doc.Descendants().Where(e => e.Name.LocalName == "tbl"))
        {
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in tbl.Elements())
            {
                row[col.Name.LocalName] = col.Value;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Parse the pipe format returned by LoadNewCourseSubjectGroup_HadDistrict.
    /// Empty result is returned by the server as the literal "[]".
    /// </summary>
    public static List<SubjectGroup> ParsePipeGroups(string? text)
    {
        var list = new List<SubjectGroup>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var trimmed = text.Trim();
        if (trimmed is "[]" or "0" or "null") return list;

        foreach (var part in trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(',');
            if (f.Length < 7) continue;
            if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) continue;
            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)) continue;
            _ = int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chapterId);
            _ = int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var subjectType);
            _ = int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index);
            _ = int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var answerCount);
            list.Add(new SubjectGroup(start, end, chapterId, subjectType, index, f[5].Trim(), answerCount));
        }
        return list;
    }

    public static int? ToInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>bool-ish server text ('true'/'false'/'0'/'1') to the SQLite 0/1 representation.</summary>
    public static int ToBoolInt(string? s) => ToBool(s) ? 1 : 0;

    public static bool ToBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        return t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1";
    }

    /// <summary>
    /// Server sends scores like "20.00"; the Access export stores "20.0".
    /// Normalise to the export's style: strip trailing zeros, always keep one decimal.
    /// </summary>
    public static string NormalizeScore(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        if (!decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return s.Trim();
        var t = d.ToString(CultureInfo.InvariantCulture);
        if (t.Contains('.'))
        {
            t = t.TrimEnd('0').TrimEnd('.');
            if (t.Length == 0 || t == "-") t = "0";
        }
        return t.Contains('.') ? t : t + ".0";
    }

    /// <summary>
    /// Server sends ISO dates with offset ("2026-06-16T16:23:50+08:00");
    /// the Access export stores "2026-06-16 16:23:50". Use DateTimeOffset so the
    /// rendered value does not depend on the host time zone.
    /// </summary>
    public static string ToDbDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = s.Trim();
        if (DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dto))
        {
            return dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        return t;
    }
}
