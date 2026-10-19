using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using Puller = JkdWeb.BankPuller;
using static AppHelpers;

sealed class QuestionBank
{
    private readonly AppPaths _paths;

    public QuestionBank(AppPaths paths)
    {
        _paths = paths;
    }

    public static void EnsureDatabase(string sqlitePath)
    {
        if (File.Exists(sqlitePath))
        {
            // 旧库补齐列：早期自建的空库缺 ctypscount / brich，会让拉取在
            // BankWriter.CopyMetadataFrom 处抛 "no such column: ctypscount"。
            EnsureBankColumns(sqlitePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath) ?? ".");
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, DefaultTimeout = 30 }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            create table if not exists course (
                icourseid integer primary key,
                ccoursename text,
                ihadbuy integer default 0,
                dchangedate text,
                dchapterchange text,
                dsubjectchange text,
                ctypscount text,
                iclassid integer,
                isubclassid integer,
                iindex integer default 0,
                bstopflag integer default 0
            );
            create table if not exists courseclass (
                iclassid integer primary key,
                ccoursecname text,
                iindex integer default 0
            );
            create table if not exists coursesubclass (
                isubclassid integer primary key,
                csubclassname text,
                iindex integer default 0
            );
            create table if not exists coursechapter (
                ichapterid integer primary key,
                icourseid integer,
                cchaptername text,
                cchaptercode text,
                igrade integer default 0,
                itype integer default 0,
                icount integer default 0,
                bstopflag integer default 0
            );
            create table if not exists coursesubjecttype (
                isubjecttype integer primary key,
                csubjectname text
            );
            create table if not exists coursesubject (
                isubjectid integer primary key,
                icourseid integer,
                ichapterid integer,
                isubjecttype integer,
                iindex integer default 0,
                ichaptertype integer default 0,
                ctitle text,
                cquestion text,
                canswer text,
                cdescription text,
                ianswercount integer default 0,
                iscore text,
                dupdatedate text,
                bstopflag integer default 0,
                brich integer
            );
            insert or ignore into coursesubjecttype(isubjecttype, csubjectname) values
                (0, '单选题'),
                (1, '多选题'),
                (2, '判断题'),
                (3, '问答题'),
                (4, '案例分析'),
                (6, '不定项选择题');
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 补齐旧库缺失的列（幂等）。只在早期"自建空库"上缺这两列；真实导出库本来就有。
    /// 失败只记日志，不阻断启动。
    /// </summary>
    public static void EnsureBankColumns(string sqlitePath)
    {
        if (!File.Exists(sqlitePath)) return;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, DefaultTimeout = 30 }.ToString());
            conn.Open();
            AddColumnIfMissing(conn, "course", "ctypscount", "text");
            AddColumnIfMissing(conn, "coursesubject", "brich", "integer");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 补齐题库列失败（忽略）: {ex.Message}");
        }
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "select count(*) from pragma_table_info($t) where name = $c";
            check.Parameters.AddWithValue("$t", table);
            check.Parameters.AddWithValue("$c", column);
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"alter table \"{table}\" add column \"{column}\" {type}";
        alter.ExecuteNonQuery();
    }

    public IEnumerable<object> GetCourses(string search, bool availableOnly, HashSet<int>? allowed = null,
        IReadOnlyCollection<string>? excludedClasses = null,
        Puller.CourseCatalogSnapshot? upstream = null)
    {
        // 按用户分配的题库范围过滤：null = 不限制；空集合 = 一门都看不到
        var allowedClause = allowed is null
            ? ""
            : allowed.Count == 0
                ? " and 1=0"
                : " and c.icourseid in (" + string.Join(",", allowed) + ")";
        // 非常规分类（默认「合作专区」）整类不展示——那些不是正式题库
        var excludedClause = "";
        if (excludedClasses is { Count: > 0 })
        {
            excludedClause = " and (cl.ccoursecname is null or cl.ccoursecname not in (" +
                             string.Join(",", excludedClasses.Select(n => "'" + n.Replace("'", "''") + "'")) + "))";
        }
        var rows = Query(@"
            select c.icourseid, c.ccoursename, c.ihadbuy, c.dchangedate, c.dchapterchange, c.dsubjectchange,
                   cl.ccoursecname, cl.iindex as class_index, sc.csubclassname, sc.iindex as subclass_index,
                   (select count(*) from coursesubject s where s.icourseid = c.icourseid and coalesce(s.bstopflag, 0)=0) as subject_count
            from course c
            left join courseclass cl on c.iclassid=cl.iclassid
            left join coursesubclass sc on c.isubclassid=sc.isubclassid
            where coalesce(c.bstopflag, 0)=0
              and (@available = 0 or ((select count(*) from coursesubject s where s.icourseid = c.icourseid and coalesce(s.bstopflag, 0)=0) > 0))"
            + allowedClause + excludedClause + @"
              and (@search = '' or c.ccoursename like @like or cl.ccoursecname like @like or sc.csubclassname like @like)
            order by cl.iindex, sc.iindex, c.ihadbuy desc, c.iindex",
            new Dictionary<string, object?> { ["@available"] = availableOnly ? 1 : 0, ["@search"] = search, ["@like"] = "%" + search + "%" });
        var list = rows.Select(r => new
        {
            id = ToInt(r["icourseid"]),
            name = ToStr(r["ccoursename"]),
            category = ToStr(r["ccoursecname"]),
            subcategory = ToStr(r["csubclassname"]),
            // 分类序号：前端按它做「一级 → 二级」分组排序（跟客户端里的 iindex 一致）
            categoryOrder = ToInt(r["class_index"]),
            subcategoryOrder = ToInt(r["subclass_index"]),
            owned = ToInt(r["ihadbuy"]) > 0,
            questionCount = ToInt(r["subject_count"]),
            changedAt = MaxDateString(r["dchangedate"], r["dchapterchange"], r["dsubjectchange"])
        }).ToList();

        /* 管理端列表额外列出「上游有、本地没有」的课程：本地没导入过（course 表里没有），
           或导入了但一门题都没拉下来。这样管理员能看到上游的全部课程、直接点「拉取」，
           不必再靠猜 ID。课程名/分类/序号取自上游课程目录（CourseCatalog）。
           学员端（availableOnly=true）**不加**：这些课本地没有正文，加了也点不开。 */
        if (upstream is not null && !availableOnly)
        {
            var localIds = list.Select(c => c.id).ToHashSet();
            var classNames = upstream.Classes.ToDictionary(c => c.Id, c => c.Name ?? "");
            var classIndexes = upstream.Classes.ToDictionary(c => c.Id, c => c.Index ?? 0);
            var subNames = upstream.SubClasses.ToDictionary(c => c.Id, c => c.Name ?? "");
            var subIndexes = upstream.SubClasses.ToDictionary(c => c.Id, c => c.Index ?? 0);
            var excluded = excludedClasses is { Count: > 0 }
                ? new HashSet<string>(excludedClasses, StringComparer.Ordinal)
                : null;
            var needle = search.Trim().ToLowerInvariant();
            foreach (var course in upstream.Courses)
            {
                if (course.Stopped) continue;
                if (localIds.Contains((int)course.Id)) continue;
                if (allowed is not null && !allowed.Contains((int)course.Id)) continue;
                var className = course.ClassId is { } cid && classNames.TryGetValue(cid, out var cn) ? cn : "";
                var subName = course.SubClassId is { } sid && subNames.TryGetValue(sid, out var sn) ? sn : "";
                // 与本地课程同一套排除规则：非常规分类（默认「合作专区」）不展示
                if (excluded is not null && className.Length > 0 && excluded.Contains(className)) continue;
                var courseName = course.Name ?? "";
                if (needle.Length > 0
                    && !courseName.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                    && !className.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                    && !subName.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                    && !course.Id.ToString(CultureInfo.InvariantCulture).Contains(needle, StringComparison.Ordinal)) continue;
                list.Add(new
                {
                    id = (int)course.Id,
                    name = courseName,
                    category = className,
                    subcategory = subName,
                    categoryOrder = course.ClassId is { } c2 && classIndexes.TryGetValue(c2, out var ci) ? ci : 0,
                    subcategoryOrder = course.SubClassId is { } s2 && subIndexes.TryGetValue(s2, out var si) ? si : 0,
                    owned = false,
                    questionCount = 0,
                    // MaxDateString 里是 Convert.ToDateTime，空串会抛；先滤成 null
                    changedAt = MaxDateString(
                        NonEmpty(course.ChangedDate), NonEmpty(course.ChapterChange), NonEmpty(course.SubjectChange))
                });
            }
        }
        return list;

        static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>这道题属于哪门课（用于按用户分配题库的越权校验；找不到返回 0）。</summary>
    public int CourseIdOfSubject(int subjectId)
    {
        var row = Query("select icourseid from coursesubject where isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = subjectId }).FirstOrDefault();
        return row is null ? 0 : ToInt(row["icourseid"]);
    }

    /// <summary>
    /// 批量查题目归属（课程 / 章节）。给全站统计用：作答记录只存题号，课程/章节每次现查，
    /// 这样换库、拉库之后归属自动跟上。查不到的题不在结果里。
    /// </summary>
    public Dictionary<int, (int CourseId, int ChapterId)> GetQuestionPlacements(IEnumerable<int> ids)
    {
        var result = new Dictionary<int, (int, int)>();
        var list = ids.Where(id => id > 0).Distinct().ToList();
        // SQLite 的参数个数有上限（默认 999），按 400 一批。
        foreach (var chunk in list.Chunk(400))
        {
            var parameters = new Dictionary<string, object?>();
            var names = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                var name = "@id" + i;
                names.Add(name);
                parameters[name] = chunk[i];
            }
            var rows = Query($"select isubjectid, icourseid, ichapterid from coursesubject where isubjectid in ({string.Join(",", names)})", parameters);
            foreach (var row in rows)
            {
                result[ToInt(row["isubjectid"])] = (ToInt(row["icourseid"]), ToInt(row["ichapterid"]));
            }
        }
        return result;
    }

    /// <summary>这门课在库里的章节/题目增量水位（没有则 null）——用于"可更新"探测。</summary>
    public (string? Chapter, string? Subject) CourseWatermarks(int courseId)
    {
        var row = Query("select dchapterchange, dsubjectchange from course where icourseid=@id",
            new Dictionary<string, object?> { ["@id"] = courseId }).FirstOrDefault();
        if (row is null) return (null, null);
        string? Clean(object? value)
        {
            var text = ToStr(value);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return (Clean(row["dchapterchange"]), Clean(row["dsubjectchange"]));
    }

    /// <summary>
    /// 健康探针：跑真实读查询（表/列齐全才算健康），不返回规模明细。
    /// 以前 health 只判"文件存在"，题库坏掉时 /api/courses 500 而 health 仍 200。
    /// </summary>
    public static bool ProbeHealthy(string sqlitePath)
    {
        if (!File.Exists(sqlitePath)) return false;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 5,
            }.ToString());
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select count(*) from course c left join courseclass cl on c.iclassid=cl.iclassid";
                cmd.ExecuteScalar();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select count(*) from coursesubject s left join coursesubjecttype t on s.isubjecttype=t.isubjecttype";
                cmd.ExecuteScalar();
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] health probe failed: {ex.Message}");
            return false;
        }
    }

    public static object TryReadTableCounts(string sqlitePath)
    {
        if (!File.Exists(sqlitePath)) return new { ok = false };
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 30 }.ToString());
            conn.Open();
            var result = new Dictionary<string, int>();
            foreach (var table in new[] { "course", "coursechapter", "coursesubject", "coursesubjecttype" })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "select count(*) from " + table;
                result[table] = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {ex}");
            return new { ok = false };
        }
    }

    public IEnumerable<object> GetChapters(int courseId)
    {
        /* 这里**故意不选** ch.icount：它是死列（脏数据），与实算普遍对不上——
           课程 20 的 213 个章节里 104 个不一致（sum(icount)=23252 vs sum(实算)=9155，
           例：章节 570671 的 icount=15 而实算 21）；全库 773 个章节里 279 个不一致
           （sum(icount)=75551 vs sum(实算)=29738）。
           questionCount 一律用下面的 subject_count 子查询实算；别再「顺手」把 icount 加回来。 */
        var rows = Query(@"
            select ch.ichapterid, ch.cchaptername, ch.cchaptercode, ch.igrade, ch.itype,
                   (select count(*) from coursesubject s where s.ichapterid=ch.ichapterid and coalesce(s.bstopflag, 0)=0) as subject_count
            from coursechapter ch
            where ch.icourseid=@courseId and coalesce(ch.bstopflag, 0)=0
            order by ch.itype, ch.cchaptercode",
            new Dictionary<string, object?> { ["@courseId"] = courseId });
        return rows.Select(r => new
        {
            id = ToInt(r["ichapterid"]),
            name = ToStr(r["cchaptername"]),
            code = ToStr(r["cchaptercode"]),
            grade = ToInt(r["igrade"]),
            type = ToInt(r["itype"]),
            questionCount = ToInt(r["subject_count"])
        }).ToList();
    }

    public IEnumerable<object> GetTypes(int courseId, int? chapterId, List<int> chapterIds)
    {
        var parameters = new Dictionary<string, object?> { ["@courseId"] = courseId };
        var sql = @"
            select s.isubjecttype, t.csubjectname, count(*) as subject_count
            from coursesubject s
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            where s.icourseid=@courseId and coalesce(s.bstopflag, 0)=0";
        if (chapterId.HasValue)
        {
            sql += " and s.ichapterid=@chapterId";
            parameters["@chapterId"] = chapterId.Value;
        }
        if (chapterIds.Count > 0) sql += " and s.ichapterid in (" + string.Join(",", chapterIds) + ")";
        sql += " group by s.isubjecttype, t.csubjectname order by s.isubjecttype";
        var rows = Query(sql, parameters);
        return rows.Select(r => new
        {
            id = ToInt(r["isubjecttype"]),
            name = ToStr(r["csubjectname"]),
            questionCount = ToInt(r["subject_count"])
        }).ToList();
    }

    public IEnumerable<object> GetQuestions(int courseId, int? chapterId, List<int> chapterIds, int? typeId, string search, string order, int? limit, List<int> ids)
    {
        var effectiveLimit = Math.Max(1, Math.Min(30000, limit ?? 120));
        var queryLimit = search.Length > 0 ? 30000 : effectiveLimit;
        if (ids.Count > 0) queryLimit = Math.Min(30000, Math.Max(queryLimit, ids.Count));
        if (chapterIds.Count > 0) queryLimit = search.Length > 0 ? queryLimit : Math.Min(30000, Math.Max(queryLimit, effectiveLimit));

        var parameters = new Dictionary<string, object?> { ["@courseId"] = courseId };
        var sql = @"
            select s.isubjectid, s.icourseid, s.ichapterid, s.isubjecttype, s.iindex, s.iscore,
                   s.ctitle, s.ianswercount, s.dupdatedate, t.csubjectname, ch.cchaptername
            from coursesubject s
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            left join coursechapter ch on s.ichapterid=ch.ichapterid
            where s.icourseid=@courseId and coalesce(s.bstopflag, 0)=0";
        if (chapterId.HasValue)
        {
            sql += " and s.ichapterid=@chapterId";
            parameters["@chapterId"] = chapterId.Value;
        }
        if (chapterIds.Count > 0) sql += " and s.ichapterid in (" + string.Join(",", chapterIds) + ")";
        if (typeId.HasValue)
        {
            sql += " and s.isubjecttype=@typeId";
            parameters["@typeId"] = typeId.Value;
        }
        if (ids.Count > 0) sql += " and s.isubjectid in (" + string.Join(",", ids) + ")";
        sql += order == "random"
            ? " order by random()"
            : " order by s.ichaptertype, ch.cchaptercode, s.iindex, s.isubjectid";
        sql += " limit " + queryLimit;
        var rows = Query(sql, parameters);
        var items = rows.Select(r => new
        {
            id = ToInt(r["isubjectid"]),
            chapterId = ToInt(r["ichapterid"]),
            chapterName = ToStr(r["cchaptername"]),
            type = ToStr(r["csubjectname"]),
            subjectType = ToInt(r["isubjecttype"]),
            index = ToInt(r["iindex"]),
            /* 卷面分值。题库里 100% 的题都有（实测 29738/29738），
               且和真题卷面完全吻合：造价管理真题 60×1 + 20×2 = 100 分。
               前端组卷算分要用，以前这个接口没带，只能拿题型写死的值。 */
            score = ToStr(r["iscore"]),
            title = StripHtml(DecryptField(r["ctitle"], r["dupdatedate"])),
            answerCount = ToInt(r["ianswercount"])
        });
        if (search.Length > 0) items = items.Where(i => i.title.Contains(search, StringComparison.OrdinalIgnoreCase));
        return items.Take(effectiveLimit).ToList();
    }

    /// <summary>
    /// 整门课的「题目 id → 章节 id」映射，扁平数组 [qid, cid, qid, cid, ...]。
    /// 前端拿它补章节树的「已做」统计：本地只记了做过的题目 id，
    /// 不知道每题属于哪一章，翻一遍题目又太慢。
    /// </summary>
    public object GetQuestionChapterPairs(int courseId)
    {
        var rows = Query(@"
            select isubjectid, ichapterid from coursesubject
            where icourseid=@courseId and coalesce(bstopflag, 0)=0",
            new Dictionary<string, object?> { ["@courseId"] = courseId });
        var flat = new List<int>(rows.Count * 2);
        foreach (var r in rows)
        {
            flat.Add(ToInt(r["isubjectid"]));
            flat.Add(ToInt(r["ichapterid"]));
        }
        return new { ok = true, courseId, count = rows.Count, pairs = flat };
    }

    public object? GetQuestion(int id)
    {
        var rows = Query(@"
            select s.*, t.csubjectname, ch.cchaptername
            from coursesubject s
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            left join coursechapter ch on s.ichapterid=ch.ichapterid
            where s.isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id });
        if (rows.Count == 0) return null;
        var r = rows[0];
        var title = NormalizeHtml(DecryptField(r["ctitle"], r["dupdatedate"]));
        var question = NormalizeHtml(DecryptField(r["cquestion"], r["dupdatedate"]));
        var answer = StripHtml(DecryptField(r["canswer"], r["dupdatedate"])).Trim();
        var description = NormalizeHtml(DecryptField(r["cdescription"], r["dupdatedate"]));
        var subjectType = ToInt(r["isubjecttype"]);
        var answerCount = ToInt(r["ianswercount"]);
        /* 题型自己就说明了「这道题有没有选项」，不能靠正则去题干里猜。
           老实现对所有题型一律跑 ExtractOptions，两种题因此坏掉：
             · 判断题(2)：库里根本没有选项（canswer 是「对」/「错」）→ options 恒为空 →
               前端按「没有 options 就是主观题」渲染成填空框 → 没有对/错按钮、
               不计分、不进错题本（实测 23657642，markResult 调用 0 次）。
             · 问答题(3)/解析题(4)/填空题(5)：答案是空串，题干里的「1. 2.」这类编号
               被误当成选项 → course 24 有 21 道问答题判成客观题，答什么都是错、
               还进错题本（实测 24082359 被解析出 5 个假选项 A/B/C/D/A）。 */
        List<object> options;
        var stem = title;
        if (subjectType is 3 or 4 or 5)
        {
            options = new List<object>();        // 主观题：一律没有选项，保持 stem/extraQuestion 原样
        }
        else if (subjectType == 2)
        {
            // text 留空：前端选项按钮画成「<b>标签</b><span>正文</span>」，判断题不需要正文
            options = new List<object> { new { label = "对", text = "" }, new { label = "错", text = "" } };
            // 判断题的 canswer 就是「对」/「错」，与合成选项的 label 同口径 → 判分/错题本/答题卡全通
        }
        else
        {
            options = ExtractOptions(title, answerCount);
            stem = options.Count > 0 ? RemoveOptions(title) : title;
            if (options.Count == 0 && question.Length > 0)
            {
                options = ExtractOptions(question, answerCount);
                if (options.Count > 0) question = "";
            }
        }
        return new
        {
            id = ToInt(r["isubjectid"]),
            courseId = ToInt(r["icourseid"]),
            chapterId = ToInt(r["ichapterid"]),
            chapterName = ToStr(r["cchaptername"]),
            type = ToStr(r["csubjectname"]),
            subjectType,
            score = ToStr(r["iscore"]),
            stem,
            extraQuestion = question,
            answer,
            description,
            options,
            updatedAt = ToDateString(r["dupdatedate"])
        };
    }

    public object GetEditorQuestions(int courseId, int? chapterId, List<int> chapterIds, string search, List<int> ids, int? limit)
    {
        var take = Math.Max(20, Math.Min(500, limit ?? 120));
        var queryLimit = ids.Count > 0 ? Math.Min(5000, Math.Max(take, ids.Count)) : search.Trim().Length > 0 ? 5000 : take;
        var parameters = new Dictionary<string, object?>();
        var sql = @"
            select s.isubjectid, s.icourseid, s.ichapterid, s.isubjecttype, s.iindex, s.ctitle, s.cquestion, s.canswer,
                   s.cdescription, s.ianswercount, s.dupdatedate, t.csubjectname, ch.cchaptername, ch.cchaptercode, ch.igrade
            from coursesubject s
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            left join coursechapter ch on s.ichapterid=ch.ichapterid
            where coalesce(s.bstopflag, 0)=0";
        if (courseId > 0)
        {
            sql += " and s.icourseid=@courseId";
            parameters["@courseId"] = courseId;
        }
        if (chapterId.HasValue && chapterId.Value > 0)
        {
            sql += " and s.ichapterid=@chapterId";
            parameters["@chapterId"] = chapterId.Value;
        }
        if (chapterIds.Count > 0) sql += " and s.ichapterid in (" + string.Join(",", chapterIds) + ")";
        if (ids.Count > 0) sql += " and s.isubjectid in (" + string.Join(",", ids) + ")";
        sql += " order by s.ichaptertype, ch.cchaptercode, s.iindex, s.isubjectid limit " + queryLimit.ToString(CultureInfo.InvariantCulture);

        var keyword = search.Trim();
        // 本地改动叠层里有记录的题，在列表里标「本地已改」（按课程缓存，避免逐题读文件）
        var editCache = new Dictionary<int, HashSet<int>>();
        string? localEditsError = null;
        bool HasLocalEdit(int cid, int sid)
        {
            if (cid <= 0) return false;
            if (!editCache.TryGetValue(cid, out var set))
            {
                try
                {
                    set = Puller.LocalEdits.EditedSubjectIds(_paths.SqlitePath, cid);
                }
                catch (Puller.LocalEditsUnavailableException ex)
                {
                    /* 叠层读不了：**不能**把"读不到"显示成"没有本地改动"
                       （否则管理员会以为改动丢了、或对着一份不完整的列表操作）。
                       列表照常返回，但把错误一并带给界面（localEditsError）。 */
                    localEditsError ??= SafeErrorText(ex);
                    set = new HashSet<int>();
                }
                editCache[cid] = set;
            }
            return set.Contains(sid);
        }
        var rows = Query(sql, parameters)
            .Select(r =>
            {
                var title = TextForEdit(r["ctitle"], r["dupdatedate"]);
                var question = TextForEdit(r["cquestion"], r["dupdatedate"]);
                var answer = TextForEdit(r["canswer"], r["dupdatedate"]);
                var description = TextForEdit(r["cdescription"], r["dupdatedate"]);
                return new
                {
                    id = ToInt(r["isubjectid"]),
                    courseId = ToInt(r["icourseid"]),
                    chapterId = ToInt(r["ichapterid"]),
                    chapterName = ToStr(r["cchaptername"]),
                    chapterCode = ToStr(r["cchaptercode"]),
                    chapterGrade = ToInt(r["igrade"]),
                    type = ToStr(r["csubjectname"]),
                    subjectType = ToInt(r["isubjecttype"]),
                    index = ToInt(r["iindex"]),
                    title = StripHtml(title),
                    rawTitle = title,
                    rawQuestion = question,
                    rawAnswer = answer,
                    rawDescription = description,
                    answer = StripHtml(answer),
                    answerCount = ToInt(r["ianswercount"]),
                    localEdited = HasLocalEdit(ToInt(r["icourseid"]), ToInt(r["isubjectid"]))
                };
            });
        if (keyword.Length > 0)
        {
            rows = rows.Where(item =>
                item.id.ToString(CultureInfo.InvariantCulture).Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.rawQuestion.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.rawAnswer.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.rawDescription.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.chapterName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        var filtered = rows.Take(take).ToList();
        return new { ok = true, items = filtered, count = filtered.Count, limit = take, localEditsError };
    }

    public object? GetEditorQuestion(int id)
    {
        var rows = Query(@"
            select s.*, c.ccoursename, t.csubjectname, ch.cchaptername, ch.cchaptercode, ch.igrade
            from coursesubject s
            left join course c on s.icourseid=c.icourseid
            left join coursesubjecttype t on s.isubjecttype=t.isubjecttype
            left join coursechapter ch on s.ichapterid=ch.ichapterid
            where s.isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id });
        if (rows.Count == 0) return null;
        var r = rows[0];
        var courseId = ToInt(r["icourseid"]);
        var chapterId = ToInt(r["ichapterid"]);
        /* 选项体检：口径与读侧同一份 ParseOptions —— 重复 / 非顺序标签、答案不在选项里。
           只对客观题（单选 0 / 多选 1 / 不定项 6）算；判断题的「对/错」和主观题本来就没有选项，
           拿选项规则去套它们只会报假警。 */
        var subjectType = ToInt(r["isubjecttype"]);
        var localEditError = (string?)null;
        var localEditMark = DescribeLocalEdit(courseId, id, out localEditError);
        var optionWarnings = subjectType is 0 or 1 or 6
            ? AuditOptions(TextForEdit(r["ctitle"], r["dupdatedate"]),
                           TextForEdit(r["cquestion"], r["dupdatedate"]),
                           TextForEdit(r["canswer"], r["dupdatedate"]),
                           ToInt(r["ianswercount"]))
            : new List<string>();
        return new
        {
            ok = true,
            id = ToInt(r["isubjectid"]),
            courseId,
            courseName = ToStr(r["ccoursename"]),
            chapterId,
            chapterName = ToStr(r["cchaptername"]),
            chapterCode = ToStr(r["cchaptercode"]),
            chapterGrade = ToInt(r["igrade"]),
            chapterPath = GetChapterPath(courseId, chapterId),
            type = ToStr(r["csubjectname"]),
            subjectType,
            title = TextForEdit(r["ctitle"], r["dupdatedate"]),
            question = TextForEdit(r["cquestion"], r["dupdatedate"]),
            answer = TextForEdit(r["canswer"], r["dupdatedate"]),
            description = TextForEdit(r["cdescription"], r["dupdatedate"]),
            answerCount = ToInt(r["ianswercount"]),
            optionWarnings,
            score = ToStr(r["iscore"]),
            updatedAt = ToDateString(r["dupdatedate"]),
            // 本地改动叠层里有记录 → 界面标「本地已改」并提供一键还原。
            // 叠层读不了 → localEdit 为 null 但**带上错误**，不伪装成"没有本地改动"。
            localEdit = localEditMark,
            localEditError
        };
    }

    /// <summary>
    /// 读"这道题有没有本地改动"。读失败不吞：返回 null 并把错误写进 <paramref name="error"/>，
    /// 由接口随响应带出去（<see cref="Puller.LocalEdits.DescribeSubject"/> 本身会抛）。
    /// </summary>
    private object? DescribeLocalEdit(int courseId, int subjectId, out string? error)
    {
        error = null;
        try
        {
            return Puller.LocalEdits.DescribeSubject(_paths.SqlitePath, courseId, subjectId);
        }
        catch (Puller.LocalEditsUnavailableException ex)
        {
            error = SafeErrorText(ex);
            return null;
        }
    }

    public object SaveEditorQuestion(Dictionary<string, object?> body, string? editor = null)
    {
        /* ⚠️ 全链路进写闸（BE-06）：编辑要先读 current / before / 章节路径（好几次 Query），
           再备份、事务写库，最后另写 JSON 叠层。旧实现没有 Enter，与拉取/整包导入交错时：
           读到的是旧库、写进的是刚换过的新库（可能改错归属）、写入 0 行也返回 ok、
           叠层顺序与库相反。SQLite 只串行物理写入，替代不了应用层互斥。
           锁顺序固定：BankWriteGate → LocalEdits.Gate（后者只在 LocalEdits 内部取，
           它不会反过来取 BankWriteGate，所以不会死锁）。 */
        using var gate = BankWriteGate.Enter();
        var id = BodyInt(body, "id");
        if (id <= 0) throw new InvalidOperationException("缺少题目 ID");
        var current = Query("select isubjectid, icourseid, ichapterid, dupdatedate from coursesubject where isubjectid=@id", new Dictionary<string, object?> { ["@id"] = id }).FirstOrDefault();
        if (current is null) throw new InvalidOperationException("题目不存在");

        /* 乐观并发（可选）：客户端带上打开这道题时读到的版本号（GetEditorQuestion 返回的
           updatedAt），与库里当前值不一致说明别人已经改过 → 拒绝，别把别人的修改盖掉。
           两位管理员同题保存没有 revision 时，至少闸保证两者串行（后写的整条覆盖前一条，
           叠层里"服务端快照"仍是最早那份，一键还原不会回到中间态）。 */
        var revision = GetBodyString(body, "revision");
        if (revision.Length > 0)
        {
            var actualRevision = ToDateString(current["dupdatedate"]);
            if (!RevisionMatches(revision, actualRevision))
            {
                throw new InvalidOperationException(
                    $"这道题在你打开之后已经被改过（库里当前版本 {actualRevision}，你手里是 {revision}）；请刷新后重新修改，避免覆盖别人的改动");
            }
        }

        var title = NormalizeEditText(GetBodyString(body, "title"));
        var question = NormalizeEditText(GetBodyString(body, "question"));
        var answer = NormalizeEditText(GetBodyString(body, "answer"));
        var description = NormalizeEditText(GetBodyString(body, "description"));
        if (title.Length == 0) throw new InvalidOperationException("题目内容不能为空");

        var courseId = ToInt(current["icourseid"]);
        var chaptersToRename = BodyChapters(body).Where(c => c.Id > 0 && c.Name.Length > 0).ToList();
        /* ⚠️ chapters[].id 以前**完全不校验**：传 {id:1} 也照收，于是能一次改掉任意
           **另一章**的名字（实测传路径外章节 570708，返回 200 且该章名被改）。
           只允许改这道题自己 chapterPath 上的章节 —— 判定与界面渲染走同一份 ChapterPathRows。 */
        if (chaptersToRename.Count > 0)
        {
            var allowedChapters = ChapterPathIds(courseId, ToInt(current["ichapterid"]));
            var illegal = chaptersToRename.Where(c => !allowedChapters.Contains(c.Id)).Select(c => c.Id).ToList();
            if (illegal.Count > 0)
            {
                throw new InvalidOperationException(
                    $"章节 {string.Join("、", illegal)} 不属于这道题的章节路径，已拒绝改名（只能改这道题所在的章）");
            }
        }
        // 记下"改动前"的内容（原样存，供一键还原用）
        var before = Query("select ctitle, cquestion, canswer, cdescription from coursesubject where isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id }).FirstOrDefault();
        var chapterNamesBefore = Query(
            "select ichapterid, cchaptername from coursechapter where ichapterid in (" +
            string.Join(",", chaptersToRename.Select(c => c.Id)) + ")",
            new Dictionary<string, object?>())
            .ToDictionary(r => ToInt(r["ichapterid"]), r => ToStr(r["cchaptername"]));

        /* 这道题的内容在主库还是拉取库？本地改动要写回它所在的那一份——遮蔽视图本身不可写。
           ⚠️ 备份的 owner 与"写库的 owner"必须是**同一份判定**（同一个连接、同一句
           select count(*) from main.coursesubject）。两条路径分别判定会出现
           "库写进了拉取库、备份却备份了主库"，那样备份里根本没有这道题的原数据。 */
        SqliteConnection.ClearAllPools();
        using var conn = OpenBank(_paths.SqlitePath, _paths.PulledSqlitePath, readOnly: false);
        var owner = MainHoldsCourse(conn, courseId) ? "main" : "pulled";
        var ownerPath = owner == "main" ? _paths.SqlitePath : _paths.PulledSqlitePath;
        // 备份**即将被改的那一份**库 + 唯一名字（旧实现永远只备份主库；题在拉取库里时
        // 备份里根本没有这道题的原数据，返回的 backup 路径不可用于恢复这次操作）
        var backupDir = BackupQuestionBank("edit-question-" + id.ToString(CultureInfo.InvariantCulture), ownerPath);

        var savedAt = NowText();
        /* ── 顺序：**先把叠层写好，再写库** ─────────────────────────────────────
           叠层写不了（权限/磁盘/被占用）→ 在动库之前抛：库保持原样、旧叠层文件完好
           （Save 走同目录临时文件 + rename），接口明确报错；不会出现旧实现那种
           "库改了、记录没写上、接口还返回 ok"。
           写库失败 → 把叠层原文写回快照，状态仍然自洽（库与记录都还是改动前）。 */
        var layerSnapshot = Puller.LocalEdits.Snapshot(_paths.SqlitePath);
        var layerTouched = false;
        try
        {
            // 记进本地改动叠层：拉取/重新导出/导入题库包后按题号贴回（本地优先），界面可一键还原
            Puller.LocalEdits.RememberSubject(_paths.SqlitePath, courseId, id,
                new Puller.LocalSubjectEdit
                {
                    Title = title,
                    Question = question,
                    Answer = answer,
                    Description = description,
                    EditedAt = savedAt,
                    Editor = editor ?? "",
                },
                before is null ? null : new Puller.LocalSubjectEdit
                {
                    Title = ToStr(before["ctitle"]),
                    Question = ToStr(before["cquestion"]),
                    Answer = ToStr(before["canswer"]),
                    Description = ToStr(before["cdescription"]),
                });
            layerTouched = true;
            foreach (var chapter in chaptersToRename)
            {
                Puller.LocalEdits.RememberChapter(_paths.SqlitePath, courseId, chapter.Id, chapter.Name,
                    chapterNamesBefore.TryGetValue(chapter.Id, out var was) ? was : null, editor);
            }

            // 复用上面那个连接与 owner 判定：备份的库 = 这次真正会写的那一份
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var chapter in BodyChapters(body))
                {
                    if (chapter.Id <= 0 || chapter.Name.Length == 0) continue;
                    using var chapterCmd = conn.CreateCommand();
                    chapterCmd.Transaction = tx;
                    chapterCmd.CommandText = $"update {owner}.coursechapter set cchaptername=@name where ichapterid=@id";
                    chapterCmd.Parameters.AddWithValue("@name", chapter.Name);
                    chapterCmd.Parameters.AddWithValue("@id", chapter.Id);
                    // 0 行 = 章节不在了却什么都没改，别当成成功
                    if (chapterCmd.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException($"章节 {chapter.Id} 的改名没有生效（章节可能已被删除），请刷新后重试");
                    }
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $@"
                    update {owner}.coursesubject
                    set ctitle=@title,
                        cquestion=@question,
                        canswer=@answer,
                        cdescription=@description,
                        ianswercount=@answerCount,
                        dupdatedate=@updatedAt
                    where isubjectid=@id";
                cmd.Parameters.AddWithValue("@title", title);
                cmd.Parameters.AddWithValue("@question", question);
                cmd.Parameters.AddWithValue("@answer", answer);
                cmd.Parameters.AddWithValue("@description", description);
                cmd.Parameters.AddWithValue("@answerCount", CountOptions(title + "\n" + question));
                cmd.Parameters.AddWithValue("@updatedAt", savedAt);
                cmd.Parameters.AddWithValue("@id", id);
                var rows = cmd.ExecuteNonQuery();
                // 0 行 = 什么都没改却返回"保存成功"（旧实现正是这样）；!= 1 一律当失败
                if (rows != 1)
                {
                    throw new InvalidOperationException($"题目更新没有生效（影响 {rows} 行），题目可能已被删除，请刷新后重试");
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }
        catch
        {
            // 库那一步没成 → 叠层退回保存前的原文（库里还是旧内容，记录也不该留下新内容）
            if (layerTouched)
            {
                try
                {
                    Puller.LocalEdits.RestoreSnapshot(_paths.SqlitePath, layerSnapshot);
                }
                catch (Exception restoreEx)
                {
                    Console.WriteLine($"[editor-save-layer-rollback-failed] subject={id} {restoreEx.Message}");
                }
            }
            throw;
        }

        return new
        {
            ok = true,
            id,
            backup = backupDir,
            backupOf = Path.GetFileName(ownerPath),
            savedAt,
            revision = savedAt,   // 下一次保存时带回来做乐观并发校验
            question = GetEditorQuestion(id)
        };
    }

    /// <summary>把本地改过的题还原成服务端版本（叠层里记的那一份），并删掉叠层记录。</summary>
    public object RestoreEditorQuestion(int id)
    {
        // 与保存同一把闸：还原要"读叠层 → 写库 → 重算计数（第二次写库）"，
        // 与拉取/导入交错会删掉还需要的记录，或让库与叠层的顺序相反（BE-06）
        using var gate = BankWriteGate.Enter();
        if (id <= 0) throw new InvalidOperationException("缺少题目 ID");
        var row = Query("select isubjectid, icourseid from coursesubject where isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id }).FirstOrDefault();
        if (row is null) throw new InvalidOperationException("题目不存在");
        var courseId = ToInt(row["icourseid"]);
        /* 还原写哪一份库、就备份哪一份（与 <see cref="RestoreSubject"/> 的目标、
           以及下面重算计数的 owner 用**同一份判定**：同一个连接 + main. 限定）。 */
        SqliteConnection.ClearAllPools();
        string owner;
        using (var conn = OpenBank(_paths.SqlitePath, _paths.PulledSqlitePath, readOnly: false))
        {
            owner = MainHoldsCourse(conn, courseId) ? "main" : "pulled";
        }
        var target = owner == "main" ? _paths.SqlitePath : _paths.PulledSqlitePath;
        // 还原会删掉叠层记录（= 丢掉"再改回去"的依据），先备份**实际会被写的那一份**库
        var backupDir = BackupQuestionBank("restore-question-" + id.ToString(CultureInfo.InvariantCulture), target);
        // 叠层读/写失败会抛（不再静默）：接口明确失败，不会出现"库还原了、记录还在"或反之
        var ok = Puller.LocalEdits.RestoreSubject(_paths.SqlitePath, target, courseId, id, out var message);
        /* ⚠️ 还原必须连 ianswercount 一起回，否则「还原 ≠ 恢复」：
           RestoreSubject 只写回 ctitle/cquestion/canswer/cdescription 四个字段，
           而 ianswercount 停在「删过选项」的那次保存上。实测 1509187 删掉 C 行保存
           （4→3）再还原：题干回到 A/B/C/D、答案回到 B，可 ianswercount 仍是 3 →
           学员端 options 被截成 A/B/C，D 消失。
           这里按**和保存路径同一口径**（CountOptions）从还原后的文本重算。 */
        if (ok) RecomputeAnswerCount(id, courseId, owner);
        return new { ok, id, message, backup = backupDir, backupOf = Path.GetFileName(target), question = ok ? GetEditorQuestion(id) : null };
    }

    /// <summary>
    /// 按保存时的同一口径重算这道题的 <c>ianswercount</c>。
    /// 保存路径写的是 <see cref="CountOptions"/>(title + "\n" + question)，回填必须同口径，
    /// 否则会出现「还原一次 / 保存一次就来回变」的抖动。
    /// <paramref name="owner"/>（"main"/"pulled"）由调用方判定 —— 与还原写库的目标必须是同一份。
    /// </summary>
    private void RecomputeAnswerCount(int id, int courseId, string owner)
    {
        var row = Query("select ctitle, cquestion, dupdatedate from coursesubject where isubjectid=@id",
            new Dictionary<string, object?> { ["@id"] = id }).FirstOrDefault();
        if (row is null) throw new InvalidOperationException("题目已不在题库里（可能已被上游下线）");
        var count = CountOptions(TextForEdit(row["ctitle"], row["dupdatedate"]) + "\n" + TextForEdit(row["cquestion"], row["dupdatedate"]));
        SqliteConnection.ClearAllPools();
        using var conn = OpenBank(_paths.SqlitePath, _paths.PulledSqlitePath, readOnly: false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"update {owner}.coursesubject set ianswercount=@count where isubjectid=@id";
        cmd.Parameters.AddWithValue("@count", count);
        cmd.Parameters.AddWithValue("@id", id);
        var rows = cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
        // 0 行 = 计数没回填却返回"还原成功"（旧实现如此），学员端下次仍会把选项截短
        if (rows != 1) throw new InvalidOperationException($"还原后重算选项数没有生效（影响 {rows} 行），请刷新后重试");
    }

    /// <summary>这门课的内容在主库（导出产物）还是在拉取库（未购课程）。</summary>
    private static bool MainHoldsCourse(SqliteConnection conn, int courseId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from main.coursesubject where icourseid=@c";
        cmd.Parameters.AddWithValue("@c", courseId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private List<object> GetChapterPath(int courseId, int chapterId)
    {
        return ChapterPathRows(courseId, chapterId)
            .Select(row => (object)new
            {
                id = row.Id,
                name = row.Name,
                code = row.Code,
                grade = row.Grade,
                questionCount = row.QuestionCount
            })
            .ToList();
    }

    /// <summary>
    /// 章节路径的原始行。<see cref="GetChapterPath"/>（给界面渲染章节输入框）与
    /// <see cref="SaveEditorQuestion"/>（校验改名的章节 id 是不是这道题自己的章）
    /// 必须走同一份判定，否则「界面能显示、后端拒绝」或反过来「后端放行越权改别人的章」。
    /// </summary>
    private List<(int Id, string Name, string Code, int Grade, int QuestionCount)> ChapterPathRows(int courseId, int chapterId)
    {
        if (courseId <= 0 || chapterId <= 0) return new List<(int Id, string Name, string Code, int Grade, int QuestionCount)>();
        var chapters = Query(@"
            select ch.ichapterid, ch.cchaptername, ch.cchaptercode, ch.igrade, ch.itype,
                   (select count(*) from coursesubject s where s.ichapterid=ch.ichapterid and coalesce(s.bstopflag,0)=0) as subject_count
            from coursechapter ch
            where ch.icourseid=@courseId and coalesce(ch.bstopflag,0)=0
            order by ch.itype, ch.cchaptercode",
            new Dictionary<string, object?> { ["@courseId"] = courseId });
        var target = chapters.FirstOrDefault(row => ToInt(row["ichapterid"]) == chapterId);
        if (target is null) return new List<(int Id, string Name, string Code, int Grade, int QuestionCount)>();
        var code = ToStr(target["cchaptercode"]);
        var type = ToInt(target["itype"]);
        return chapters
            .Where(row => ToInt(row["itype"]) == type && code.StartsWith(ToStr(row["cchaptercode"]), StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => ToInt(row["igrade"]))
            .ThenBy(row => ToStr(row["cchaptercode"]).Length)
            .Select(row => (ToInt(row["ichapterid"]), ToStr(row["cchaptername"]), ToStr(row["cchaptercode"]), ToInt(row["igrade"]), ToInt(row["subject_count"])))
            .ToList();
    }

    /// <summary>这道题所在章节路径上的章节 id 集合 —— 改章节名只允许动这些章。</summary>
    private HashSet<int> ChapterPathIds(int courseId, int chapterId)
        => ChapterPathRows(courseId, chapterId).Select(row => row.Id).ToHashSet();

    /// <summary>
    /// 备份**即将被修改的那一份**库：题在主库就备份主库，题在拉取库就备份拉取库
    /// （旧实现永远只备份主库 —— 题写进拉取库时返回的 backup 里没有这道题的原数据，
    /// 等于"给了个恢复不了这次操作的备份"）。
    /// 用 SQLite 在线备份 API 取一致快照；名字用 <see cref="TimestampUnique"/>
    /// （秒级时间戳在同一秒内连存两次会覆盖同一份快照）。
    /// </summary>
    private string BackupQuestionBank(string reason, string ownerDbPath)
    {
        var owner = Path.GetFullPath(ownerDbPath);
        if (!File.Exists(owner)) throw new FileNotFoundException("SQLite question bank not found", owner);
        var dataRoot = Directory.GetParent(_paths.SqlitePath)?.FullName ?? _paths.BaseRoot;
        // 备份目录名里带上"备份的是哪一份库"，人工恢复时不会拿错（bank / bank-pulled）
        var ownerTag = Path.GetFileNameWithoutExtension(owner).Replace("question-bank", "bank", StringComparison.OrdinalIgnoreCase);
        var backupDir = Path.Combine(dataRoot, "_backups", CleanBackupName(reason) + "-" + ownerTag + "-" + TimestampUnique());
        Directory.CreateDirectory(backupDir);
        SqliteSnapshot.Copy(owner, Path.Combine(backupDir, Path.GetFileName(owner)));
        BackupRetention.Prune(Path.Combine(dataRoot, "_backups"));
        return backupDir;
    }

    private static string CleanBackupName(string value)
    {
        value = Regex.Replace(value, @"[^\w\-]+", "-").Trim('-');
        return value.Length == 0 ? "edit" : value.Length > 80 ? value[..80] : value;
    }

    /// <summary>
    /// 版本号（<c>dupdatedate</c>）比对，给保存路径做乐观并发用。
    /// 两边先按读路径同一口径（<see cref="ToDateString"/>）比字符串，再退一步按时间解析比 ——
    /// 客户端可能回传 "2026-07-02 15:10:02" / 带 T / 带毫秒的写法。
    /// </summary>
    private static bool RevisionMatches(string expected, string actual)
    {
        var left = expected.Trim();
        var right = actual.Trim();
        if (left == right) return true;
        return DateTime.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
               && DateTime.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b)
               && a == b;
    }

    private static int BodyInt(Dictionary<string, object?> body, string key)
    {
        if (!body.TryGetValue(key, out var value) || value is null) return 0;
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number)) return number;
            return 0;
        }
        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) ? parsed : 0;
    }

    private static List<(int Id, string Name)> BodyChapters(Dictionary<string, object?> body)
    {
        if (!body.TryGetValue("chapters", out var value) || value is null) return new List<(int, string)>();
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Array) return new List<(int, string)>();
        var result = new List<(int, string)>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = 0;
            var name = "";
            if (item.TryGetProperty("id", out var idElement))
            {
                if (idElement.ValueKind == JsonValueKind.Number) idElement.TryGetInt32(out id);
                else if (idElement.ValueKind == JsonValueKind.String) int.TryParse(idElement.GetString(), out id);
            }
            if (item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                name = NormalizeEditText(nameElement.GetString() ?? "");
            }
            if (id > 0) result.Add((id, name));
        }
        return result;
    }

    private static string TextForEdit(object? value, object? updateDate)
    {
        return NormalizeAssetPaths(DecryptField(value, updateDate) ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }

    private static string NormalizeEditText(string value)
    {
        return (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
    }

    /* ---- 选项解析：读 / 数 / 保存 / 还原 / 重贴的**唯一口径** ---------------------------
       ⚠️ 以前这里有**两套**口径，而且写侧用的是更粗的那套：
         · 读侧 ExtractOptions：行首标签之外还切行内标签；
         · 写侧 CountOptions（保存 / 还原 / 重贴共用）：只数**行首**标签、只按去重计数。
       于是「A. one B. two / C. three D. four」这种同行写法：读侧能解析出 4 个选项，
       写侧只写 ianswercount=2 → 学员端 Take(2) 把 C/D 截掉 → 正确答案的按钮根本不存在。
       反过来，题干里引用了一个标签（「A. see B. clause」后面才是真正的 B. 行）时，
       读侧把引用也当成选项（A/B/B/C/D），写侧写 4 → 截成 A/B/B/C，D 又被砍掉。
       现在只有一份解析（ParseOptions）：读取、计数、保存、还原、重贴全用它，
       计数**不再用来截断**真实选项 —— 它只是"这道题有几个选项"的元数据。
       （拉取层不能引用本工程，LocalEdits.cs 里有一份逐字等价的镜像，改这里必须同步那边，
         有 scripts/verify-puller-sync.ps1 兜底。） */

    /// <summary>一个解析出来的选项。</summary>
    private sealed record OptionItem(string Label, string Text);

    /// <summary>规范解析结果 + 给编辑器看的诊断（重复 / 非顺序标签）。</summary>
    private sealed record OptionParse(List<OptionItem> Options, List<string> Warnings);

    /// <summary>
    /// 题干（HTML 或纯文本）→ 规范选项列表。规则：
    ///   1. 行首标签是选项的骨架；同一标签重复出现时只有第一次算选项，后面那几行并进上一个选项的
    ///      正文（实测 22421001 的选项块后面又跟了一遍 C./D. 行、24298363 跟着 B./D. 开头的解释行）。
    ///   2. 行内标签只有「不是任何行首标签、且还没出现过」时才当成选项分隔点 ——
    ///      题干里引用标签（「A. see B. clause」后面才是真正的 B. 行）不会被切碎。
    ///   3. 文本一律先过 StripHtml（<br> → 换行、去标签、HtmlDecode），\r 归一成 \n。
    /// </summary>
    private static OptionParse ParseOptions(string? html)
    {
        var text = StripHtml(html ?? "").Replace("\r", "\n");
        var warnings = new List<string>();

        var lineLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var heads = new List<Match>();
        foreach (Match match in OptionLabelRegex.Matches(text))
        {
            var label = match.Groups[1].Value.ToUpperInvariant();
            if (!lineLabels.Add(label))
            {
                warnings.Add($"选项标签 {label} 重复出现：只有第一次算选项，后面那几行并入上一个选项的正文");
                continue;
            }
            heads.Add(match);
        }

        var options = new List<OptionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < heads.Count; i++)
        {
            var start = heads[i].Index + heads[i].Length;
            var end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
            SplitOptionSegment(heads[i].Groups[1].Value, text.Substring(start, Math.Max(0, end - start)),
                options, seen, lineLabels);
        }

        var labels = options.Select(option => option.Label.ToUpperInvariant()).ToList();
        if (labels.Count > 0)
        {
            var missing = new List<string>();
            for (var i = 0; i < labels.Count; i++)
            {
                var expected = ((char)('A' + i)).ToString();
                if (!string.Equals(labels[i], expected, StringComparison.Ordinal)) missing.Add(expected);
            }
            if (missing.Count > 0)
            {
                warnings.Add($"选项标签不是 A 起连续编号：解析出 {string.Join("、", labels)}" +
                             $"（期望 A、B、C…；缺 {string.Join("、", missing)}）");
            }
        }
        return new OptionParse(options, warnings);
    }

    /// <summary>
    /// 题干里能数出几个选项 —— 直接数 <see cref="ParseOptions"/> 的规范结果
    /// （不再是"行首标签去重个数"：同行写法的选项会被那个口径漏掉）。
    /// 2..8 之外一律记 0，保持旧语义：0 = 不作约束，学员端不截断。
    /// </summary>
    private static int CountOptions(string? text)
    {
        var count = ParseOptions(text).Options.Count;
        return count is >= 2 and <= 8 ? count : 0;
    }

    /// <summary>
    /// 编辑器用的选项体检：解析口径与读侧完全一致（题干优先，题干里解析不出选项再看补充题干），
    /// 把「重复 / 非顺序标签、答案不在选项里、库里计数与解析结果不一致」直接摆给编辑看。
    /// </summary>
    private static List<string> AuditOptions(string? title, string? question, string? answer, int storedCount)
    {
        var parsed = ParseOptions(title);
        var source = "题干";
        if (parsed.Options.Count == 0)
        {
            var fromQuestion = ParseOptions(question);
            if (fromQuestion.Options.Count > 0)
            {
                parsed = fromQuestion;
                source = "补充题干";
            }
        }

        var warnings = new List<string>(parsed.Warnings);
        var labels = parsed.Options.Select(option => option.Label.ToUpperInvariant()).ToList();
        var answerLabels = Regex.Matches(answer ?? "", "[A-H]")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (labels.Count > 0)
        {
            var absent = answerLabels.Where(label => !labels.Contains(label, StringComparer.Ordinal)).ToList();
            if (absent.Count > 0)
            {
                warnings.Add($"正确答案 {string.Join("、", absent)} 不在{source}解析出的选项" +
                             $"（{string.Join("、", labels)}）里：学员端选不中正确答案");
            }
        }
        if (storedCount > 0 && labels.Count > 0 && storedCount != labels.Count)
        {
            warnings.Add($"库里的 ianswercount={storedCount} 与{source}解析出的 {labels.Count} 个选项不一致" +
                         "（保存后按解析结果回写）");
        }
        return warnings;
    }

    private List<Dictionary<string, object?>> Query(string sql, Dictionary<string, object?> parameters)
    {
        if (!File.Exists(_paths.SqlitePath)) throw new FileNotFoundException("SQLite question bank not found. Upload or mount data/question-bank.db first.", _paths.SqlitePath);
        // 读路径统一走 OpenBank：主库 + 拉取库（未购课程）合起来看，不带库名的表名会命中遮蔽视图。
        using var conn = OpenBank(_paths.SqlitePath, _paths.PulledSqlitePath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var pair in parameters) cmd.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var result = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    private static string DecryptField(object? value, object? updateDate)
    {
        var text = ToStr(value);
        if (text.Length == 0) return "";
        var decrypted = Decrypt(text, GetTicks(updateDate));
        return decrypted.Length > 0 ? decrypted : text;
    }

    private static string GetTicks(object? value)
    {
        if (value is null || value is DBNull) return "";
        return Convert.ToDateTime(value, CultureInfo.InvariantCulture).Ticks.ToString(CultureInfo.InvariantCulture);
    }

    private static string Decrypt(string encrypted, string suffix)
    {
        try
        {
            var key = suffix.Length == 0 ? "china" : suffix;
            var encryptedBytes = Convert.FromBase64String(encrypted);
            var legacyEncoding = Encoding.GetEncoding("GB18030");
            var keyBytes = legacyEncoding.GetBytes(key);
            using var md5 = MD5.Create();
            using var triple = TripleDES.Create();
            triple.Key = md5.ComputeHash(keyBytes);
            triple.Mode = CipherMode.ECB;
            triple.Padding = PaddingMode.PKCS7;
            using var transform = triple.CreateDecryptor();
            var plain = transform.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
            return legacyEncoding.GetString(plain);
        }
        catch
        {
            return "";
        }
    }

    /* 选项标签出现在「行首」的形式（老口径，不变）。 */
    private static readonly Regex OptionLabelRegex = new(@"(?m)(?:^|\n)\s*([A-H])[\.\u3001\uff0e:：]\s*");

    /* 行内标签：同一行里写了两个及以上选项（实测 1509065 的「A.11. 34 B. 11. 33」、
       1509104 的「A.甲 B.乙 C、丙 D. 丁」、1512051 的「…B. F1-F3-F2 C, …  D. F3-F1-F2」）。
       分隔符后**不加**空白要求 —— 实测 1509100 的「132 D、66; 99」在 D 后面直接跟内容，
       加了 \s 就切不开，而它的正确答案正是 D（选项里没有 D，学员无法作答）。
       标签前的边界：空白（会被当分隔符吃掉）**或**一个汉字（零宽，必须保留 ——
       实测 1509074 的「合理的施工组织设计C.生产要素消耗水平」中间没有空格，
       老口径整个 C 选项被吞进 B 的正文，全库 1600+ 道题受影响）。 */
    private static readonly Regex InlineOptionLabelRegex = new(@"(?:\s|(?<=[\u4e00-\u9fff]))([A-H])[\.\u3001\uff0e:：]\s*");

    /// <summary>
    /// 学员端读侧：题干 → 选项。与 <see cref="CountOptions"/> 共用 <see cref="ParseOptions"/>，
    /// 所以"写进去的计数"与"读出来的选项数"不可能再对不上。
    /// <paramref name="answerCount"/> 保留在签名里（调用点与探针都按它反射），但**不再用来截断**：
    /// 库里那个数可能是旧保存路径写错的小值（或上游就没跟上题干），拿它 Take() 会砍掉真实选项。
    /// 实测整库扫描：解析数 > 库里计数 的行只有 2 条，而那 2 条恰恰是库里计数错的。
    /// </summary>
    private static List<object> ExtractOptions(string html, int answerCount)
    {
        _ = answerCount;
        return ParseOptions(html).Options
            .Select(option => (object)new { label = option.Label, text = option.Text })
            .ToList();
    }

    /// <summary>
    /// 把「行首标签 + 整段正文」再按**行内标签**切开：同一行写了两个选项时，后一个会被
    /// 前一个吞进正文（实测 1509065 的 A 里混进「11. 34 B.」、1509100 的正确答案 D
    /// 根本不在选项里、1509104 只剩 1 个选项 → 学员无法作答）。
    /// 只切「既不是行首标签、也没出现过」的标签，避免把正文里引用的标签当成分隔点。
    /// </summary>
    private static void SplitOptionSegment(string label, string body, List<OptionItem> parsed,
        HashSet<string> seen, HashSet<string> lineLabels)
    {
        var pendingLabel = label;
        var rest = body;
        while (true)
        {
            var cut = FindInlineOptionLabel(rest, seen, pendingLabel, lineLabels);
            var head = (cut is null ? rest : rest[..cut.Index]).Trim();
            if (head.Length > 0) parsed.Add(new OptionItem(pendingLabel, head));
            seen.Add(pendingLabel);
            if (cut is null) return;
            pendingLabel = cut.Groups[1].Value;
            rest = rest[(cut.Index + cut.Length)..];
        }
    }

    private static Match? FindInlineOptionLabel(string body, HashSet<string> seen, string pendingLabel, HashSet<string> lineLabels)
    {
        for (var match = InlineOptionLabelRegex.Match(body); match.Success; match = match.NextMatch())
        {
            var label = match.Groups[1].Value.ToUpperInvariant();
            if (lineLabels.Contains(label)) continue;   // 是行首标签 → 正文里的标签引用，不是分隔点
            if (seen.Contains(label)) continue;         // 已经出现过（含正在拼的这一段）
            if (label.Equals(pendingLabel, StringComparison.OrdinalIgnoreCase)) continue;
            return match;
        }
        return null;
    }

    private static string RemoveOptions(string html)
    {
        /* 题干里的选项列表是按行排的（NormalizeHtml 已经把 \n 转成 <br>），
           所以按 <br> 切行，找到第一行「以 A. / B、 开头」的，把它和后面的都丢掉。

           这里**不能**再走 StripHtml + HtmlEncode 的老路子 —— 那会把题干里的
           <img> 连同 HTML 一起丢掉。线上有 2237 道题的题图挂在题干上，凡是题干
           里带选项列表的，题图全被那一步吃掉；像
             <img src=".../1509093_0.jpg">\n \nA. 109.9 B. 110.3\n...
           这种「题干就是一张图」的题，整段 stem 直接变成空串。 */
        var parts = Regex.Split(html, @"(?i)(<br\s*/?>)");
        var cut = -1;
        for (var i = 0; i < parts.Length; i += 2)
        {
            if (IsOptionLineStart(parts[i])) { cut = i; break; }
        }
        if (cut < 0) return html;
        if (cut == 0) return "";                 // 整段就是选项，没有题干
        var kept = string.Concat(parts.Take(cut)).Trim();
        return kept.Length > 0 ? kept : html;
    }

    private static bool IsOptionLineStart(string line)
    {
        var text = StripHtml(line).TrimStart();
        return Regex.IsMatch(text, @"^[A-H][\.\u3001\uff0e:：]\s*");
    }

    private static string NormalizeHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = NormalizeAssetPaths(html);
        return html.Replace("\r\n", "\n").Replace("\n", "<br>");
    }

    private static string NormalizeAssetPaths(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = html.Replace("\\", "/", StringComparison.Ordinal);
        html = Regex.Replace(html, @"(?i)(src=[""']?)(?:file:///)?[a-z]:/[^""'>\s]*?/jinkaodian/data/", "$1/assets/");
        html = Regex.Replace(html, @"(?i)(src=[""']?)(?:file:///android_asset/)?(?:\.\./)+data/", "$1/assets/");
        html = Regex.Replace(html, @"(?i)(src=[""']?)(?:file:///android_asset/)?data/", "$1/assets/");
        return html;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<.*?>", "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static int ToInt(object? value) => value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static string ToStr(object? value) => value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    private static string ToDateString(object? value) => value is null or DBNull ? "" : Convert.ToDateTime(value, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// 只读查询入口。给 <see cref="ChapterMapStore"/> 用——章节映射重建要扫全库。
    /// </summary>
    public List<Dictionary<string, object?>> ReadAll(string sql, Dictionary<string, object?>? parameters = null)
        => Query(sql, parameters ?? new Dictionary<string, object?>());

    /// <summary>
    /// 题干纯文本：解密 → 去标签 → 压空白。
    /// 章节映射按它建键，建表与查表必须走同一个函数，否则永远匹配不上。
    /// </summary>
    public static string PlainStem(object? title, object? updateDate)
        => Regex.Replace(StripHtml(DecryptField(title, updateDate)), @"\s+", " ").Trim();

    /// <summary>解析的纯文本。口径同 <see cref="PlainStem"/>，但读的是 cdescription。</summary>
    public static string PlainDescription(object? description, object? updateDate)
        => Regex.Replace(StripHtml(DecryptField(description, updateDate)), @"\s+", " ").Trim();

    /// <summary>
    /// 章节映射的键：题干去掉所有非中文/字母/数字的字符后取前 120 个。
    /// 去标点空白是为了让「同一道题在不同卷册里的排版差异」不影响匹配。
    /// </summary>
    public static string StemKey(string plainStem) => TextKey(plainStem, 120);

    /// <summary>解析的键。解析比题干长，取前 300 个字符。</summary>
    public static string DescriptionKey(string plainDescription) => TextKey(plainDescription, 300);

    /// <summary>把一段纯文本压成匹配键：只保留中文/字母/数字，截断到 maxChars。</summary>
    public static string TextKey(string plain, int maxChars)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var sb = new StringBuilder(maxChars);
        foreach (var ch in plain)
        {
            if (sb.Length >= maxChars) break;
            if ((ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '\u4e00' && ch <= '\u9fff'))
            {
                sb.Append(ch >= 'A' && ch <= 'Z' ? (char)(ch + 32) : ch);
            }
        }
        return sb.ToString();
    }

    private static string MaxDateString(params object?[] values)
    {
        DateTime? max = null;
        foreach (var value in values)
        {
            if (value is null or DBNull) continue;
            var date = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
            if (!max.HasValue || date > max.Value) max = date;
        }
        return max.HasValue ? max.Value.ToString("yyyy-MM-dd HH:mm:ss") : "";
    }
}
