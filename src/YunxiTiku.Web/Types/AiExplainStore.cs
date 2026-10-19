using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>
/// AI 解析的同题缓存。同一道题、同一模型、同一提示词只向上游要一次 ——
/// 这既是省钱，也是让「反复点解析」不会每次都等十几秒。
///
/// 缓存键 = SHA256(模型 + 提示词版本 + 题号)：
///   · 换模型或改提示词 → 键变 → 自然失效，不需要手工清缓存；
///   · 题号本身不加密进键，库里也存题号，方便按题排查。
/// 与统计库同样是**独立文件**（<c>ai-explain.db</c>）：题库整包替换时不受影响。
/// </summary>
sealed class AiExplainStore
{
    private readonly AppPaths _paths;
    private readonly object _initLock = new();
    private volatile bool _schemaReady;

    public AiExplainStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string DbPath
    {
        get
        {
            var full = Path.GetFullPath(_paths.SqlitePath);
            var dir = Path.GetDirectoryName(full) ?? _paths.BaseRoot;
            return Path.Combine(dir, "ai-explain.db");
        }
    }

    private SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
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
                create table if not exists ai_explain (
                    ikey text primary key,
                    isubjectid integer not null,
                    smodel text not null default '',
                    sanswer text not null,
                    dupdated text not null
                );
                create index if not exists ix_ai_explain_subject on ai_explain(isubjectid);";
            cmd.ExecuteNonQuery();
            _schemaReady = true;
        }
    }

    public string? Get(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select sanswer from ai_explain where ikey=@k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Put(string key, int questionId, string model, string answer)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            insert into ai_explain (ikey, isubjectid, smodel, sanswer, dupdated)
            values (@k, @q, @m, @a, @t)
            on conflict(ikey) do update set sanswer=excluded.sanswer, dupdated=excluded.dupdated";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@q", questionId);
        cmd.Parameters.AddWithValue("@m", model);
        cmd.Parameters.AddWithValue("@a", answer);
        cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public (int Entries, int Questions) Stats()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select count(*), count(distinct isubjectid) from ai_explain";
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }
}
