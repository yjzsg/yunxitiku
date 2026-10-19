using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>
/// 全站答题统计（匿名聚合）。每题一行：作答题数 / 答错次数。
///
/// 几个刻意的决定：
/// ① **独立库**（<c>question-stats.db</c>，与题库同目录）。题库文件是「整包上传会替换」的产物，
///    统计写进去会随每次换库被抹掉；这里跟拉取库一样另起一个文件，互不影响。
/// ② **不存任何身份**：没有用户名、没有 IP、没有会话 id，只有 (题号, 对/错) 的累计计数。
///    所以它不可能被用来还原某个人的作答记录，也不受「删除账号」影响。
/// ③ **小样本不外露**：作答题数不足 <see cref="QuestionMinAttempts"/> 的题，读接口直接不返回。
///    用户基数小的时候「3 次作答 67% 答错」这种数字会误导，也会让「谁答错了」变得可猜。
/// </summary>
sealed class QuestionStatsStore
{
    /// <summary>一道题至少被作答多少次才对外给比例。低于这个数只累计、不展示。</summary>
    public const int QuestionMinAttempts = 3;

    private readonly AppPaths _paths;
    private readonly object _initLock = new();
    private bool _schemaReady;

    public QuestionStatsStore(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>统计库路径：与题库同目录、独立文件（见类注释 ①）。</summary>
    public string StatsPath
    {
        get
        {
            var full = Path.GetFullPath(_paths.SqlitePath);
            var dir = Path.GetDirectoryName(full) ?? _paths.BaseRoot;
            return Path.Combine(dir, "question-stats.db");
        }
    }

    private SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(StatsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StatsPath,
            DefaultTimeout = 15,
        }.ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "pragma journal_mode=wal; pragma synchronous=normal; pragma busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        EnsureSchema(conn);
        return conn;
    }

    private void EnsureSchema(SqliteConnection conn)
    {
        if (_schemaReady) return;
        lock (_initLock)
        {
            if (_schemaReady) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                create table if not exists question_stat (
                    isubjectid integer primary key,
                    icourseid integer not null default 0,
                    ichapterid integer not null default 0,
                    iattempts integer not null default 0,
                    iwrong integer not null default 0,
                    dupdated text
                );
                create index if not exists ix_question_stat_course on question_stat(icourseid);";
            cmd.ExecuteNonQuery();
            _schemaReady = true;
        }
    }

    /// <summary>
    /// 记一批作答（只累计计数）。题目归属的课程/章节每次现查题库：拉取库、换库之后
    /// 归属都可能变，把 0 当作「还不知道」在下次写入时补上（见 upsert 的 case 分支）。
    /// </summary>
    public int Record(IReadOnlyList<(int QuestionId, bool Correct)> items, QuestionBank bank)
    {
        var valid = items.Where(item => item.QuestionId > 0).ToList();
        if (valid.Count == 0) return 0;
        var placements = bank.GetQuestionPlacements(valid.Select(item => item.QuestionId));
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            insert into question_stat(isubjectid, icourseid, ichapterid, iattempts, iwrong, dupdated)
            values ($id, $course, $chapter, 1, $wrong, $now)
            on conflict(isubjectid) do update set
                iattempts = question_stat.iattempts + 1,
                iwrong = question_stat.iwrong + excluded.iwrong,
                icourseid = case when question_stat.icourseid = 0 then excluded.icourseid else question_stat.icourseid end,
                ichapterid = case when question_stat.ichapterid = 0 then excluded.ichapterid else question_stat.ichapterid end,
                dupdated = excluded.dupdated";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pCourse = cmd.Parameters.Add("$course", SqliteType.Integer);
        var pChapter = cmd.Parameters.Add("$chapter", SqliteType.Integer);
        var pWrong = cmd.Parameters.Add("$wrong", SqliteType.Integer);
        var pNow = cmd.Parameters.Add("$now", SqliteType.Text);
        pNow.Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var (questionId, correct) in valid)
        {
            placements.TryGetValue(questionId, out var place);
            pId.Value = questionId;
            pCourse.Value = place.CourseId;
            pChapter.Value = place.ChapterId;
            pWrong.Value = correct ? 0 : 1;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return valid.Count;
    }

    /// <summary>批量取单题统计（只返回达到最小样本量的题）。</summary>
    public Dictionary<int, (int Attempts, int Wrong)> GetQuestions(IEnumerable<int> ids)
    {
        var list = ids.Where(id => id > 0).Distinct().Take(200).ToList();
        var result = new Dictionary<int, (int, int)>();
        if (list.Count == 0) return result;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var names = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            var name = "$id" + i;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, list[i]);
        }
        cmd.CommandText = $@"select isubjectid, iattempts, iwrong from question_stat
            where isubjectid in ({string.Join(",", names)}) and iattempts >= {QuestionMinAttempts}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetInt32(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        }
        return result;
    }

    /// <summary>一门课的整体作答统计（错题率的分母是次数，不是题数）。</summary>
    public (int Attempts, int Wrong) GetCourse(int courseId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select coalesce(sum(iattempts), 0), coalesce(sum(iwrong), 0) from question_stat where icourseid = $course";
        cmd.Parameters.AddWithValue("$course", courseId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
