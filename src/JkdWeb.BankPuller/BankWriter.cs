using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JkdWeb.BankPuller;

/// <summary>One row of coursechapter after mapping to the site's SQLite schema.</summary>
public sealed record ChapterRow(
    long ChapterId, long CourseId, string? Name, string? Code,
    int? Grade, int? Type, int? Count, int StopFlag);

/// <summary>One row of coursesubject after mapping to the site's SQLite schema.</summary>
public sealed record SubjectRow(
    long SubjectId, long CourseId, long ChapterId, int? SubjectType, int? ChapterType, int? Index,
    string? Score, string? Title, string? Question, string? Answer, int? AnswerCount,
    string? Description, string? UpdatedDate, int StopFlag, int Rich)
{
    /// <summary>Server flag (bpicquestion || bpicanswer): row may carry images. Not stored in SQLite.</summary>
    public bool HasPictureFlag { get; init; }
}

/// <summary>
/// Writes pulled data into a site-compatible SQLite question bank.
/// Schema DDL is byte-identical to the one shipped by JkdWeb.Core / YunxiTiku.Web.
/// </summary>
public sealed class BankWriter : IDisposable
{
    private readonly SqliteConnection _cn;

    public string DbPath { get; }

    public BankWriter(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        }.ToString());
        _cn.Open();
        EnsureSchema();
    }

    public void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS course (
              icourseid integer primary key,
              ccoursename text,
              ihadbuy integer,
              dchangedate text,
              dchapterchange text,
              dsubjectchange text,
              ctypscount text,
              iclassid integer,
              isubclassid integer,
              bstopflag integer,
              iindex integer
            );
            CREATE TABLE IF NOT EXISTS coursechapter (
              ichapterid integer primary key,
              icourseid integer,
              cchaptername text,
              cchaptercode text,
              igrade integer,
              itype integer,
              icount integer,
              bstopflag integer
            );
            CREATE TABLE IF NOT EXISTS courseclass (
              iclassid integer primary key,
              ccoursecname text,
              iindex integer
            );
            CREATE TABLE IF NOT EXISTS coursesubclass (
              isubclassid integer primary key,
              csubclassname text,
              iindex integer
            );
            CREATE TABLE IF NOT EXISTS coursesubject (
              isubjectid integer primary key,
              icourseid integer,
              ichapterid integer,
              isubjecttype integer,
              ichaptertype integer,
              iindex integer,
              iscore text,
              ctitle text,
              cquestion text,
              canswer text,
              ianswercount integer,
              cdescription text,
              dupdatedate text,
              bstopflag integer,
              brich integer
            );
            CREATE TABLE IF NOT EXISTS coursesubjecttype (
              isubjecttype integer primary key,
              csubjectname text
            );
            CREATE INDEX IF NOT EXISTS idx_chapter_course on coursechapter(icourseid, bstopflag, itype, cchaptercode);
            CREATE INDEX IF NOT EXISTS idx_course_stop on course(bstopflag, ihadbuy, iindex);
            CREATE INDEX IF NOT EXISTS idx_subject_chapter on coursesubject(ichapterid, bstopflag);
            CREATE INDEX IF NOT EXISTS idx_subject_course on coursesubject(icourseid, bstopflag, ichapterid, isubjecttype, iindex);
            CREATE INDEX IF NOT EXISTS idx_subject_type on coursesubject(icourseid, isubjecttype, bstopflag);
            """);
    }

    /// <summary>Copy the course-level metadata tables from another bank (reference data, not course content).</summary>
    public void CopyMetadataFrom(string sourceDbPath, long? courseId)
    {
        var src = Path.GetFullPath(sourceDbPath);
        if (!File.Exists(src)) throw new FileNotFoundException("metadata source db not found", src);

        Exec("ATTACH DATABASE $src AS meta", ("$src", src));
        try
        {
            Exec("""
                INSERT OR REPLACE INTO courseclass   (iclassid, ccoursecname, iindex)             SELECT iclassid, ccoursecname, iindex FROM meta.courseclass;
                INSERT OR REPLACE INTO coursesubclass(isubclassid, csubclassname, iindex)         SELECT isubclassid, csubclassname, iindex FROM meta.coursesubclass;
                INSERT OR REPLACE INTO coursesubjecttype(isubjecttype, csubjectname)              SELECT isubjecttype, csubjectname FROM meta.coursesubjecttype;
                """);
            if (courseId is { } cid)
            {
                Exec("""
                    INSERT OR REPLACE INTO course
                      (icourseid, ccoursename, ihadbuy, dchangedate, dchapterchange, dsubjectchange,
                       ctypscount, iclassid, isubclassid, bstopflag, iindex)
                    SELECT icourseid, ccoursename, ihadbuy, dchangedate, dchapterchange, dsubjectchange,
                           ctypscount, iclassid, isubclassid, bstopflag, iindex
                    FROM meta.course WHERE icourseid = $cid;
                    """, ("$cid", cid));
            }
        }
        finally
        {
            Exec("DETACH DATABASE meta");
        }
    }

    public void DeleteCourseContent(long courseId)
    {
        Exec("DELETE FROM coursechapter WHERE icourseid = $c", ("$c", courseId));
        Exec("DELETE FROM coursesubject WHERE icourseid = $c", ("$c", courseId));
    }

    public int InsertChapters(IEnumerable<ChapterRow> rows)
    {
        var n = 0;
        using var tx = _cn.BeginTransaction();
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO coursechapter
              (ichapterid, icourseid, cchaptername, cchaptercode, igrade, itype, icount, bstopflag)
            VALUES ($id, $cid, $name, $code, $grade, $type, $count, $stop);
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pCid = cmd.Parameters.Add("$cid", SqliteType.Integer);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pCode = cmd.Parameters.Add("$code", SqliteType.Text);
        var pGrade = cmd.Parameters.Add("$grade", SqliteType.Integer);
        var pType = cmd.Parameters.Add("$type", SqliteType.Integer);
        var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);
        var pStop = cmd.Parameters.Add("$stop", SqliteType.Integer);

        foreach (var r in rows)
        {
            pId.Value = r.ChapterId;
            pCid.Value = r.CourseId;
            pName.Value = (object?)r.Name ?? DBNull.Value;
            pCode.Value = (object?)r.Code ?? DBNull.Value;
            pGrade.Value = (object?)r.Grade ?? DBNull.Value;
            pType.Value = (object?)r.Type ?? DBNull.Value;
            pCount.Value = (object?)r.Count ?? DBNull.Value;
            pStop.Value = r.StopFlag;
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    public int InsertSubjects(IEnumerable<SubjectRow> rows)
    {
        var n = 0;
        using var tx = _cn.BeginTransaction();
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO coursesubject
              (isubjectid, icourseid, ichapterid, isubjecttype, ichaptertype, iindex, iscore,
               ctitle, cquestion, canswer, ianswercount, cdescription, dupdatedate, bstopflag, brich)
            VALUES ($id, $cid, $chid, $stype, $ctype, $idx, $score,
                    $title, $q, $a, $ac, $desc, $upd, $stop, $rich);
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pCid = cmd.Parameters.Add("$cid", SqliteType.Integer);
        var pChid = cmd.Parameters.Add("$chid", SqliteType.Integer);
        var pStype = cmd.Parameters.Add("$stype", SqliteType.Integer);
        var pCtype = cmd.Parameters.Add("$ctype", SqliteType.Integer);
        var pIdx = cmd.Parameters.Add("$idx", SqliteType.Integer);
        var pScore = cmd.Parameters.Add("$score", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pQ = cmd.Parameters.Add("$q", SqliteType.Text);
        var pA = cmd.Parameters.Add("$a", SqliteType.Text);
        var pAc = cmd.Parameters.Add("$ac", SqliteType.Integer);
        var pDesc = cmd.Parameters.Add("$desc", SqliteType.Text);
        var pUpd = cmd.Parameters.Add("$upd", SqliteType.Text);
        var pStop = cmd.Parameters.Add("$stop", SqliteType.Integer);
        var pRich = cmd.Parameters.Add("$rich", SqliteType.Integer);

        foreach (var r in rows)
        {
            pId.Value = r.SubjectId;
            pCid.Value = r.CourseId;
            pChid.Value = r.ChapterId;
            pStype.Value = (object?)r.SubjectType ?? DBNull.Value;
            pCtype.Value = (object?)r.ChapterType ?? DBNull.Value;
            pIdx.Value = (object?)r.Index ?? DBNull.Value;
            pScore.Value = (object?)r.Score ?? DBNull.Value;
            pTitle.Value = (object?)r.Title ?? DBNull.Value;
            pQ.Value = (object?)r.Question ?? DBNull.Value;
            pA.Value = (object?)r.Answer ?? DBNull.Value;
            pAc.Value = (object?)r.AnswerCount ?? DBNull.Value;
            pDesc.Value = (object?)r.Description ?? DBNull.Value;
            pUpd.Value = (object?)r.UpdatedDate ?? DBNull.Value;
            pStop.Value = r.StopFlag;
            pRich.Value = r.Rich;
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    public void SetCourseWatermarks(long courseId, string? chapterChange, string? subjectChange)
    {
        if (!string.IsNullOrEmpty(chapterChange))
        {
            Exec("UPDATE course SET dchapterchange = $v WHERE icourseid = $c",
                ("$v", chapterChange), ("$c", courseId));
        }
        if (!string.IsNullOrEmpty(subjectChange))
        {
            Exec("UPDATE course SET dsubjectchange = $v WHERE icourseid = $c",
                ("$v", subjectChange), ("$c", courseId));
        }
    }

    public long Count(string table, long courseId)
    {
        using var cmd = _cn.CreateCommand();
        var col = table == "coursechapter" ? "icourseid" : "icourseid";
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {col} = $c";
        cmd.Parameters.AddWithValue("$c", courseId);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _cn.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
