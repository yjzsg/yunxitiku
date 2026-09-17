using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace JkdWeb.BankPuller;

/// <summary>题库编辑器里的一次本地修改（题干/解析等）。</summary>
public sealed class LocalSubjectEdit
{
    public string? Title { get; set; }
    public string? Question { get; set; }
    public string? Answer { get; set; }
    public string? Description { get; set; }
    public string? EditedAt { get; set; }
    public string? Editor { get; set; }

    /// <summary>覆盖前那一份（服务端）内容，供"还原为服务端版本"用。</summary>
    public string? ServerTitle { get; set; }
    public string? ServerQuestion { get; set; }
    public string? ServerAnswer { get; set; }
    public string? ServerDescription { get; set; }
}

/// <summary>一次"重贴"的结果：贴回多少题/多少章节，以及有多少记录因为题目在新库里已不存在而搁浅。</summary>
public sealed record ReapplyOutcome(int Subjects, int Chapters, int Orphans)
{
    public int Total => Subjects + Chapters;

    public string Describe()
    {
        if (Total == 0 && Orphans == 0) return "无本地改动";
        var text = $"重贴本地改动 {Subjects} 题";
        if (Chapters > 0) text += $" / {Chapters} 个章节";
        if (Orphans > 0) text += $"；{Orphans} 处改动对应的题目已不在新题库里（记录保留，未丢弃）";
        return text;
    }
}

/// <summary>
/// 本地改动叠层：题库编辑器对题干/解析/章节名的修改记在这里（题库目录下的 local-edits.json）。
///
/// 为什么用文件而不是表：主库会被"重新导出题库"整库重建、也会被"整包上传"整体替换，
/// 写进主库的改动会随库一起丢。这个 JSON 放在题库目录里、不在那两条链路的替换范围内，
/// 所以拉取 / 重新导出之后调用 <see cref="Reapply"/> 就能把本地改动按题号贴回去（本地优先）。
/// </summary>
public static class LocalEdits
{
    private static readonly object Gate = new();

    private sealed class ChapterEdit
    {
        public string? Name { get; set; }
        public string? EditedAt { get; set; }
        public string? Editor { get; set; }
        public string? ServerName { get; set; }
    }

    private sealed class CourseEdits
    {
        public Dictionary<string, LocalSubjectEdit> Subjects { get; set; } = new();
        public Dictionary<string, ChapterEdit> Chapters { get; set; } = new();
    }

    /// <summary>叠层文件与主库同目录（主库与拉取库共用一份叠层）。</summary>
    public static string FilePathFor(string bankPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bankPath)) ?? ".", "local-edits.json");

    private static string CourseKey(int courseId) => courseId.ToString(CultureInfo.InvariantCulture);

    // ---- 记录本地改动 ----------------------------------------------------

    /// <summary>题库编辑器保存后调用：记下这次本地修改。<paramref name="server"/> 只在首次记录时采用。</summary>
    public static void RememberSubject(string bankPath, int courseId, int subjectId, LocalSubjectEdit local, LocalSubjectEdit? server)
    {
        lock (Gate)
        {
            var all = Load(bankPath);
            if (!all.TryGetValue(CourseKey(courseId), out var course))
            {
                course = new CourseEdits();
                all[CourseKey(courseId)] = course;
            }
            var key = subjectId.ToString(CultureInfo.InvariantCulture);
            var existed = course.Subjects.TryGetValue(key, out var previous);
            if (!existed) previous = new LocalSubjectEdit();
            previous!.Title = local.Title;
            previous.Question = local.Question;
            previous.Answer = local.Answer;
            previous.Description = local.Description;
            previous.EditedAt = local.EditedAt;
            previous.Editor = local.Editor;
            // 首次记录才存"服务端原样"，避免把上一次的本地修改当成服务端版本
            if (!existed)
            {
                previous.ServerTitle = server?.Title;
                previous.ServerQuestion = server?.Question;
                previous.ServerAnswer = server?.Answer;
                previous.ServerDescription = server?.Description;
            }
            course.Subjects[key] = previous;
            Save(bankPath, all);
        }
    }

    public static void RememberChapter(string bankPath, int courseId, int chapterId, string name, string? serverName, string? editor)
    {
        lock (Gate)
        {
            var all = Load(bankPath);
            if (!all.TryGetValue(CourseKey(courseId), out var course))
            {
                course = new CourseEdits();
                all[CourseKey(courseId)] = course;
            }
            var key = chapterId.ToString(CultureInfo.InvariantCulture);
            var existed = course.Chapters.TryGetValue(key, out var previous);
            if (!existed) previous = new ChapterEdit();
            previous!.Name = name;
            previous.EditedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            previous.Editor = editor;
            if (!existed) previous.ServerName = serverName;
            course.Chapters[key] = previous;
            Save(bankPath, all);
        }
    }

    // ---- 查询（给界面用）-------------------------------------------------

    /// <summary>某门课里所有有本地改动的题号（给题库编辑列表打「本地已改」标记用）。</summary>
    public static HashSet<int> EditedSubjectIds(string bankPath, int courseId)
    {
        lock (Gate)
        {
            var ids = new HashSet<int>();
            var all = Load(bankPath);
            if (!all.TryGetValue(CourseKey(courseId), out var course)) return ids;
            foreach (var key in course.Subjects.Keys)
            {
                if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) ids.Add(id);
            }
            return ids;
        }
    }

    /// <summary>这道题是否有本地改动；返回 null 表示没有。</summary>
    public static object? DescribeSubject(string bankPath, int courseId, int subjectId)
    {
        lock (Gate)
        {
            var all = Load(bankPath);
            if (!all.TryGetValue(CourseKey(courseId), out var course)) return null;
            if (!course.Subjects.TryGetValue(subjectId.ToString(CultureInfo.InvariantCulture), out var edit)) return null;
            return new { editedAt = edit.EditedAt ?? "", editor = edit.Editor ?? "" };
        }
    }

    // ---- 重贴 / 还原 -----------------------------------------------------

    /// <summary>
    /// 把某门课的本地改动贴回目标库（拉取、重新导出之后调用）。
    /// 会先读一遍当前库里的值刷新"服务端版本"，所以"还原"始终回到最新的服务端内容。
    /// 题目在新库里已不存在时保留记录（界面显示为"题目已下线"），不静默丢弃。
    /// </summary>
    public static ReapplyOutcome Reapply(string bankPath, string targetDb, int courseId)
    {
        if (!File.Exists(targetDb)) return new ReapplyOutcome(0, 0, 0);
        lock (Gate)
        {
            var all = Load(bankPath);
            if (!all.TryGetValue(CourseKey(courseId), out var course)) return new ReapplyOutcome(0, 0, 0);
            if (course.Subjects.Count == 0 && course.Chapters.Count == 0) return new ReapplyOutcome(0, 0, 0);

            var subjects = 0;
            var chapters = 0;
            var orphans = 0;
            SqliteConnection.ClearAllPools();
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(targetDb),
                DefaultTimeout = 30,
            }.ToString());
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var (key, edit) in course.Subjects)
                {
                    if (!int.TryParse(key, out var subjectId)) continue;
                    var server = ReadSubject(conn, tx, subjectId);
                    if (server is null)
                    {
                        // 新库里没有这道题了：保留记录（界面显示为"题目已下线"），不静默丢弃
                        orphans++;
                        continue;
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = """
                            update coursesubject
                               set ctitle=@t, cquestion=@q, canswer=@a, cdescription=@d, dupdatedate=@at
                             where isubjectid=@id
                            """;
                        cmd.Parameters.AddWithValue("@t", edit.Title ?? server.Value.Title);
                        cmd.Parameters.AddWithValue("@q", edit.Question ?? server.Value.Question);
                        cmd.Parameters.AddWithValue("@a", edit.Answer ?? server.Value.Answer);
                        cmd.Parameters.AddWithValue("@d", edit.Description ?? server.Value.Description);
                        cmd.Parameters.AddWithValue("@at", edit.EditedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        cmd.Parameters.AddWithValue("@id", subjectId);
                        cmd.ExecuteNonQuery();
                    }
                    edit.ServerTitle = server.Value.Title;
                    edit.ServerQuestion = server.Value.Question;
                    edit.ServerAnswer = server.Value.Answer;
                    edit.ServerDescription = server.Value.Description;
                    subjects++;
                }

                foreach (var (key, edit) in course.Chapters)
                {
                    if (!int.TryParse(key, out var chapterId)) continue;
                    var serverName = ReadChapterName(conn, tx, chapterId);
                    if (serverName is null || string.IsNullOrEmpty(edit.Name)) continue;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "update coursechapter set cchaptername=@n where ichapterid=@id";
                        cmd.Parameters.AddWithValue("@n", edit.Name);
                        cmd.Parameters.AddWithValue("@id", chapterId);
                        cmd.ExecuteNonQuery();
                    }
                    edit.ServerName = serverName;
                    chapters++;
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
            Save(bankPath, all);
            return new ReapplyOutcome(subjects, chapters, orphans);
        }
    }

    /// <summary>把这道题还原成服务端版本（用叠层里记的那一份），并删掉叠层记录。</summary>
    public static bool RestoreSubject(string bankPath, string targetDb, int courseId, int subjectId, out string message)
    {
        message = "";
        lock (Gate)
        {
            var all = Load(bankPath);
            if (!all.TryGetValue(CourseKey(courseId), out var course) ||
                !course.Subjects.TryGetValue(subjectId.ToString(CultureInfo.InvariantCulture), out var edit))
            {
                message = "这道题没有本地改动记录";
                return false;
            }
            if (!File.Exists(targetDb))
            {
                message = "题库文件不存在";
                return false;
            }

            SqliteConnection.ClearAllPools();
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(targetDb),
                DefaultTimeout = 30,
            }.ToString());
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    update coursesubject
                       set ctitle=@t, cquestion=@q, canswer=@a, cdescription=@d
                     where isubjectid=@id
                    """;
                cmd.Parameters.AddWithValue("@t", edit.ServerTitle ?? "");
                cmd.Parameters.AddWithValue("@q", edit.ServerQuestion ?? "");
                cmd.Parameters.AddWithValue("@a", edit.ServerAnswer ?? "");
                cmd.Parameters.AddWithValue("@d", edit.ServerDescription ?? "");
                cmd.Parameters.AddWithValue("@id", subjectId);
                var rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                {
                    message = "题目已不在题库里（可能已被上游下线）";
                    return false;
                }
            }
            course.Subjects.Remove(subjectId.ToString(CultureInfo.InvariantCulture));
            // 这门课没有别的本地改动了就把空壳也去掉，别在文件里留垃圾
            if (course.Subjects.Count == 0 && course.Chapters.Count == 0) all.Remove(CourseKey(courseId));
            Save(bankPath, all);
            message = "已还原为服务端版本";
            return true;
        }
    }

    // ---- 内部 -----------------------------------------------------------

    private static (string Title, string Question, string Answer, string Description)? ReadSubject(SqliteConnection conn, SqliteTransaction tx, int subjectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "select ctitle, cquestion, canswer, cdescription from coursesubject where isubjectid=@id";
        cmd.Parameters.AddWithValue("@id", subjectId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        string At(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);
        return (At(0), At(1), At(2), At(3));
    }

    private static string? ReadChapterName(SqliteConnection conn, SqliteTransaction tx, int chapterId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "select cchaptername from coursechapter where ichapterid=@id";
        cmd.Parameters.AddWithValue("@id", chapterId);
        var value = cmd.ExecuteScalar();
        return value is null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static Dictionary<string, CourseEdits> Load(string bankPath)
    {
        try
        {
            var path = FilePathFor(bankPath);
            if (!File.Exists(path)) return new Dictionary<string, CourseEdits>(StringComparer.Ordinal);
            var json = File.ReadAllText(path, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<Dictionary<string, CourseEdits>>(json);
            return data is null ? new Dictionary<string, CourseEdits>(StringComparer.Ordinal) : new(data, StringComparer.Ordinal);
        }
        catch
        {
            // 读坏了就当没有本地改动，绝不因此阻断拉取
            return new Dictionary<string, CourseEdits>(StringComparer.Ordinal);
        }
    }

    private static void Save(string bankPath, Dictionary<string, CourseEdits> data)
    {
        try
        {
            var path = FilePathFor(bankPath);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        catch
        {
            // Best-effort：叠层写不进去不影响题库本身
        }
    }
}
