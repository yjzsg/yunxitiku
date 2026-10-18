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

/// <summary>
/// 「整卷题 → 课本章」的外挂映射。
///
/// 背景：题库的 coursechapter 是四层（卷册 grade0 → 章 grade1 → 节 grade2 → 目 grade3）。
/// 章节练习 / 章节密训 / 章节精选 这类卷册把题挂在「节」上，天然带课本章归属；
/// 而 历年真题 / 模考 / 预测 / 高频考点 这类是**整卷**，题直接挂在「卷」上，
/// 题库里根本没有章节这个维度（14259 题，占全库 48%）。
///
/// 好在整卷题里有相当一部分在章节型卷册里有一份**完全相同的题干**，那份已经归好章了 ——
/// 按题干查表就能把章节抄过来（实测 6967 题，其中 564 题一题多章）。
/// 刻意不做相似度猜测：那部分留一验证只有 81~88% 的精度，错归会直接污染薄弱章节统计。
///
/// 为什么不写进 question-bank.db：整包上传会替换那个文件，写进去就没了。
/// 所以落成 data/chapter-map.json 这个独立文件，题库更新后由管理端重建。
/// </summary>
sealed class ChapterMapStore
{
    private readonly AppPaths _paths;
    private readonly QuestionBank _bank;
    private readonly IConfiguration? _configuration;
    private readonly object _gate = new();
    private ChapterMapFile? _cache;
    private string _cacheKey = "";

    public ChapterMapStore(AppPaths paths, QuestionBank bank, IConfiguration? configuration = null)
    {
        _paths = paths;
        _bank = bank;
        _configuration = configuration;
    }

    /// <summary>
    /// 重建的规模上限（题数）。超过就直接放弃，不冒险。
    ///
    /// 为什么需要：重建会把「主库 + 拉取库」的全部题目读进内存建索引。
    /// 本机开发库几万题没问题，但拉取库可能是整个上游目录 ——
    /// 实测某套部署的 <c>jkd-pulled.db</c> 有 4.68GB / 521 万题 / 659 门课，
    /// 那种规模下这个算法会直接吃光内存。宁可没有映射，也不能把进程搞崩。
    /// 可用 <c>App:ChapterMapMaxQuestions</c> 调整。
    /// </summary>
    private int MaxQuestions
    {
        get
        {
            var configured = _configuration?.GetValue<int?>("App:ChapterMapMaxQuestions");
            return configured is > 0 ? configured.Value : 500_000;
        }
    }

    public string FilePath
    {
        get
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_paths.SqlitePath)) ?? _paths.BaseRoot;
            return Path.Combine(dir, "chapter-map.json");
        }
    }

    /// <summary>题库文件签名。整包上传或重新拉取后签名会变，映射随之过期。</summary>
    public string BankSignature()
    {
        static string Of(string path)
        {
            if (!File.Exists(path)) return "-";
            var info = new FileInfo(path);
            return info.Length.ToString(CultureInfo.InvariantCulture)
                   + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        return Of(_paths.SqlitePath) + "|" + Of(_paths.PulledSqlitePath);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>读映射（带缓存）。文件缺失或读坏一律返回空映射，不抛。</summary>
    public ChapterMapFile Load()
    {
        var key = CacheKey();
        lock (_gate)
        {
            if (_cache is not null && _cacheKey == key) return _cache;
            _cache = TryRead() ?? new ChapterMapFile();
            _cacheKey = key;
            return _cache;
        }
    }

    private string CacheKey()
    {
        var path = FilePath;
        var mapStamp = File.Exists(path)
            ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)
            : "-";
        return BankSignature() + "|" + mapStamp;
    }

    private ChapterMapFile? TryRead()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;
            var parsed = JsonSerializer.Deserialize<ChapterMapFile>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            if (parsed is null) return null;
            parsed.Chapters ??= new List<string>();
            parsed.Map ??= new Dictionary<string, int[]>(StringComparer.Ordinal);
            parsed.BankSignature ??= "";
            parsed.GeneratedAt ??= "";
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    public object Status()
    {
        var map = Load();
        var stale = map.BankSignature.Length > 0 && map.BankSignature != BankSignature();
        return new
        {
            ok = true,
            present = map.Map.Count > 0,
            stale,
            generatedAt = map.GeneratedAt,
            chapters = map.Chapters.Count,
            mappedQuestions = map.MappedQuestions,
            multiChapterQuestions = map.MultiChapterQuestions,
            file = FilePath,
        };
    }

    /// <summary>
    /// 页码信号的采信门槛：同一页码下，最高票那个章至少要占全部票数的这个比例。
    /// 低于门槛说明这一页跨了好几个章（页码在每节重新开始），猜了基本是错的。
    /// </summary>
    private const double PageVoteConfidence = 0.9;

    /// <summary>
    /// 解析里的教材页码引用，如「2025教材P346」「参见教材P176」。
    /// 整卷题里 27.6% 的解析带这个，而页码和章节几乎一一对应 ——
    /// 用它反推章节，留一验证 99.3% 命中。
    /// </summary>
    private static readonly Regex PageRefRegex = new(@"(?:(\d{4})\s*教材|教材)\s*[Pp]\s*(\d{1,4})", RegexOptions.Compiled);

    /// <summary>没有「教材」字样时的兜底：「参见 P176」。</summary>
    private static readonly Regex PageAnyRegex = new(@"[Pp]\s*(\d{1,4})", RegexOptions.Compiled);

    /// <summary>取解析里的页码引用。年份为空表示没写明年份（跨版本时用页码兜底）。</summary>
    private static List<(string Year, int Page)> PageRefs(string description)
    {
        var result = new List<(string, int)>();
        if (description.Length == 0) return result;
        foreach (Match match in PageRefRegex.Matches(description))
        {
            if (int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page > 0)
            {
                result.Add((match.Groups[1].Value, page));
            }
        }
        if (result.Count > 0) return result;
        foreach (Match match in PageAnyRegex.Matches(description))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page > 0)
            {
                result.Add(("", page));
            }
        }
        return result;
    }

    /// <summary>
    /// 章名的规范形式：把任意空白（含全角空格 U+3000）压成一个半角空格再 trim。
    ///
    /// 题库里同一个课本的章在不同卷册里会用不同的空格写法 —— 实测课程 570 同时存在
    /// 「第二章　安装工程计量」(U+3000) 和「第二章 安装工程计量」(半角)，
    /// 全库去掉空白后重名的章有 102 组、涉及 414 个 chapter 节点。
    /// 不归一化就会被当成两个不同的章，一题多章的统计直接虚高。
    ///
    /// 前端的 textbookChapterName 必须做同样的归一化，否则两边对不上键。
    /// </summary>
    public static string NormalizeChapterName(string? name)
        => Regex.Replace(name ?? "", @"\s+", " ").Trim();

    private sealed class ChapterNode
    {
        public int Id { get; init; }
        public int CourseId { get; init; }
        public int Type { get; init; }
        public int Grade { get; init; }
        public string Name { get; init; } = "";
        public string Code { get; init; } = "";
        public int ParentId { get; set; }
    }

    /// <summary>扫全库重建映射并落盘。</summary>
    public object Rebuild()
    {
        if (!File.Exists(_paths.SqlitePath))
        {
            throw new FileNotFoundException("题库不存在，先上传或挂载 data/question-bank.db", _paths.SqlitePath);
        }

        // 先数一遍题量再决定要不要往下走：读全库建索引是内存大户，超限直接放弃。
        var totalQuestions = Convert.ToInt32(
            _bank.ReadAll("select count(*) as n from coursesubject where coalesce(bstopflag,0)=0")
                .FirstOrDefault()?.GetValueOrDefault("n") ?? 0,
            CultureInfo.InvariantCulture);
        if (totalQuestions > MaxQuestions)
        {
            throw new InvalidOperationException(
                $"题库共 {totalQuestions} 题，超过章节映射的重建上限 {MaxQuestions} 题，已跳过。"
                + "映射需要把全部题目读进内存建索引，这个规模会吃光内存。"
                + "如果确实要建，调大 App:ChapterMapMaxQuestions。");
        }

        var chapterRows = _bank.ReadAll(
            "select ichapterid, icourseid, cchaptername, cchaptercode, igrade, itype "
            + "from coursechapter where coalesce(bstopflag,0)=0 order by itype, cchaptercode");
        var questionRows = _bank.ReadAll(
            "select isubjectid, icourseid, ichapterid, ctitle, cdescription, dupdatedate "
            + "from coursesubject where coalesce(bstopflag,0)=0");

        var nodes = new List<ChapterNode>(chapterRows.Count);
        foreach (var row in chapterRows)
        {
            nodes.Add(new ChapterNode
            {
                Id = AsInt(row, "ichapterid"),
                CourseId = AsInt(row, "icourseid"),
                Type = AsInt(row, "itype"),
                Grade = AsInt(row, "igrade"),
                Name = AsText(row, "cchaptername"),
                Code = AsText(row, "cchaptercode"),
            });
        }
        var byId = new Dictionary<int, ChapterNode>(nodes.Count);
        foreach (var node in nodes) byId[node.Id] = node;

        // 父节点判定与前端 buildChapterTree 的**意图**一致：
        // 同课程 + 同 itype + grade 差 1 + 父 code 是子 code 的前缀且更短，
        // 并在多个候选里取 **code 最长**的那个（前端是 .sort(b.code.length - a.code.length)[0]）。
        // 取第一个匹配是不对的：题库里同一层级可能有两个前缀都成立，选错就会挂到别的章下。
        //
        // 但两边**并非逐字一致**：前端 app.js 是 node.code.startsWith(item.code)（大小写敏感），
        // 这里（以及 GetChapterSubtree 里那处）用的是 StartsWith(..., OrdinalIgnoreCase)（不敏感）。
        // 当前题库 773/773 条 cchaptercode 全是纯数字，两种语义逐行算父节点差异 0 行，因此实际等价。
        // 若将来出现含字母的 code，前后端行为就会分叉：例如 "0139A02".startsWith("0139a") 前端为
        // false、后端为 true。届时再统一语义，而不是现在改比较逻辑——改了没收益，还可能引入回归。
        foreach (var node in nodes)
        {
            if (node.Code.Length == 0) continue;
            ChapterNode? best = null;
            foreach (var candidate in nodes)
            {
                if (candidate.Id == node.Id) continue;
                if (candidate.CourseId != node.CourseId || candidate.Type != node.Type) continue;
                if (candidate.Grade != node.Grade - 1) continue;
                if (candidate.Code.Length == 0 || candidate.Code.Length >= node.Code.Length) continue;
                if (!node.Code.StartsWith(candidate.Code, StringComparison.OrdinalIgnoreCase)) continue;
                if (best is null || candidate.Code.Length > best.Code.Length) best = candidate;
            }
            if (best is not null) node.ParentId = best.Id;
        }

        var pathCache = new Dictionary<int, List<ChapterNode>>();
        List<ChapterNode> PathOf(int chapterId)
        {
            if (pathCache.TryGetValue(chapterId, out var cached)) return cached;
            var chain = new List<ChapterNode>();
            var current = byId.TryGetValue(chapterId, out var node) ? node : null;
            var guard = 0;
            while (current is not null && guard++ < 16)
            {
                chain.Insert(0, current);
                current = current.ParentId != 0 && byId.TryGetValue(current.ParentId, out var parent) ? parent : null;
            }
            pathCache[chapterId] = chain;
            return chain;
        }

        // 第一遍：给每道题算出章节路径，同时记下哪些卷册带「节」（depth >= 3）。
        var parsed = new List<(int Id, int CourseId, int VolumeId, string Stem, string Desc, List<ChapterNode> Path)>(questionRows.Count);
        var volumesWithSections = new HashSet<(int, int)>();
        foreach (var row in questionRows)
        {
            var path = PathOf(AsInt(row, "ichapterid"));
            if (path.Count < 2) continue;
            var courseId = AsInt(row, "icourseid");
            if (path.Count >= 3) volumesWithSections.Add((courseId, path[0].Id));
            parsed.Add((AsInt(row, "isubjectid"), courseId, path[0].Id,
                QuestionBank.StemKey(QuestionBank.PlainStem(row.GetValueOrDefault("ctitle"), row.GetValueOrDefault("dupdatedate"))),
                QuestionBank.DescriptionKey(QuestionBank.PlainDescription(row.GetValueOrDefault("cdescription"), row.GetValueOrDefault("dupdatedate"))),
                path));
        }

        // 第二遍：章节型卷册（有节）当语料，建三个索引，按可靠性从高到低用：
        //   ① 题干指纹   —— 同一道题的排版差异最小
        //   ② 解析指纹   —— 题干有细微差异时（题号、空格、选项换行）还能对上
        //   ③ 教材页码   —— 解析里「2025教材P346」这种引用，反推出的章节留一验证 99.4% 命中
        // 索引都必须**按课程**分：不同课程可能有完全相同的题干/页码却归在不同章下。
        // 同一题干在同一课程内命中多个章 = 一题多章。
        var stemIndex = new Dictionary<(int CourseId, string Key), HashSet<string>>();
        var descIndex = new Dictionary<(int CourseId, string Key), HashSet<string>>();
        var pageIndex = new Dictionary<(int CourseId, string Year, int Page), Dictionary<string, int>>();
        var corpusRows = 0;
        var corpusEmptyStem = 0;
        foreach (var row in parsed)
        {
            // 语料来自**章节型**卷册（有节的那些），所以这里要 !Contains —— 反过来就一行都取不到。
            if (!volumesWithSections.Contains((row.CourseId, row.VolumeId))) continue;
            if (row.Path.Count < 3) continue;   // 只取挂在「节」上的题
            corpusRows++;
            if (row.Stem.Length == 0)
            {
                corpusEmptyStem++;
                continue;
            }
            var chapter = NormalizeChapterName(row.Path[1].Name);

            Add(stemIndex, (row.CourseId, row.Stem), chapter);
            if (row.Desc.Length > 0) Add(descIndex, (row.CourseId, row.Desc), chapter);
            foreach (var (year, page) in PageRefs(row.Desc))
            {
                var key = (row.CourseId, year, page);
                if (!pageIndex.TryGetValue(key, out var votes)) pageIndex[key] = votes = new Dictionary<string, int>(StringComparer.Ordinal);
                votes[chapter] = votes.GetValueOrDefault(chapter) + 1;
            }
        }

        static void Add(Dictionary<(int, string), HashSet<string>> index, (int, string) key, string chapter)
        {
            if (!index.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                index[key] = set;
            }
            set.Add(chapter);
        }

        // 第三遍：整卷型卷册（无节）的题，查表抄章节。
        var chapterNames = new List<string>();
        var chapterIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int IndexOf(string name)
        {
            if (chapterIndex.TryGetValue(name, out var i)) return i;
            chapterNames.Add(name);
            chapterIndex[name] = chapterNames.Count - 1;
            return chapterNames.Count - 1;
        }

        var map = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var multi = 0;
        var volumeRows = 0;
        var viaStem = 0;
        var viaDesc = 0;
        var viaPage = 0;
        foreach (var row in parsed)
        {
            if (volumesWithSections.Contains((row.CourseId, row.VolumeId))) continue;
            volumeRows++;

            HashSet<string>? chapters = null;
            var source = "";
            if (row.Stem.Length > 0 && stemIndex.TryGetValue((row.CourseId, row.Stem), out var byStemHit) && byStemHit.Count > 0)
            {
                chapters = byStemHit;
                source = "stem";
            }
            if (chapters is null && row.Desc.Length > 0 && descIndex.TryGetValue((row.CourseId, row.Desc), out var byDescHit) && byDescHit.Count > 0)
            {
                chapters = byDescHit;
                source = "desc";
            }
            if (chapters is null && row.Desc.Length > 0)
            {
                // 页码：同一页可能对应多个章（页码在每节重新开始，低页码尤其混）。
                // 只在投票够集中时才采信 —— 实测线上题库 1566 个 (课程,年份,页码) 键里
                // 99.0% 的最高票占比 ≥0.9，剩下那 0.6% 猜出来的基本是错的
                // （例：某题解析写「见教材第一章第一节P1」，P1 在语料里散在三个章，
                // 多数票投给了第五章）。宁可留空，也不写错。
                var best = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (year, page) in PageRefs(row.Desc))
                {
                    if (!pageIndex.TryGetValue((row.CourseId, year, page), out var votes) || votes.Count == 0) continue;
                    var total = votes.Values.Sum();
                    var top = votes.OrderByDescending(pair => pair.Value).First();
                    if (total <= 0 || top.Value / (double)total < PageVoteConfidence) continue;
                    best[top.Key] = Math.Max(best.GetValueOrDefault(top.Key), top.Value);
                }
                if (best.Count > 0)
                {
                    chapters = new HashSet<string>(best.Keys, StringComparer.Ordinal);
                    source = "page";
                }
            }
            if (chapters is null || chapters.Count == 0) continue;

            var indices = chapters.Select(IndexOf).OrderBy(x => x).ToArray();
            map[row.Id.ToString(CultureInfo.InvariantCulture)] = indices;
            if (indices.Length > 1) multi++;
            if (source == "stem") viaStem++;
            else if (source == "desc") viaDesc++;
            else viaPage++;
        }

        var payload = new ChapterMapFile
        {
            Version = 1,
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            BankSignature = BankSignature(),
            Chapters = chapterNames,
            Map = map,
            MappedQuestions = map.Count,
            MultiChapterQuestions = multi,
        };

        var filePath = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? _paths.BaseRoot);
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, filePath, true);

        lock (_gate)
        {
            _cache = payload;
            _cacheKey = CacheKey();
        }

        var volumeQuestions = parsed.Count(r => !volumesWithSections.Contains((r.CourseId, r.VolumeId)));
        return new
        {
            ok = true,
            chapters = chapterNames.Count,
            sectionQuestions = parsed.Count - volumeQuestions,
            volumeQuestions,
            mappedQuestions = map.Count,
            multiChapterQuestions = multi,
            // 诊断用：三条信号各命中多少、语料两侧的规模。
            // 匹配为 0 时这些数字能立刻区分「索引没建起来」和「题库里确实没有可用的重复题」。
            corpusRows,
            corpusEmptyStem,
            corpusStems = stemIndex.Count,
            corpusDescs = descIndex.Count,
            corpusPages = pageIndex.Count,
            volumeRows,
            viaStem,
            viaDesc,
            viaPage,
            sampleStem = parsed.FirstOrDefault(r => r.Stem.Length > 0).Stem,
            file = filePath,
            message = $"已重建章节映射：{volumeQuestions} 道整卷题中归入 {map.Count} 道"
                      + (multi > 0 ? $"（其中 {multi} 道一题多章）" : ""),
        };
    }

    /// <summary>
    /// 题库换过（拉取更新 / 整包上传）之后重建映射。
    /// 失败只记日志、不往上抛 —— 拉取本身已经成功了，不能因为映射重建失败就让整个操作报错。
    /// </summary>
    public bool TryRebuildQuietly(string reason)
    {
        try
        {
            Rebuild();
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [chapter-map] {reason}后已重建章节映射");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [chapter-map] {reason}后重建失败（不影响题库本身）: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 启动自检：映射文件缺失或已过期就在**后台**补一次，免得首次部署还要手动点重建。
    /// 只做一次、失败不重试，也不阻塞启动。
    /// </summary>
    public void WarmUpInBackground()
    {
        if (!File.Exists(_paths.SqlitePath)) return;
        var map = Load();
        if (map.Map.Count > 0 && map.BankSignature == BankSignature()) return;
        Task.Run(() => TryRebuildQuietly(map.Map.Count == 0 ? "启动自检（映射缺失）" : "启动自检（映射过期）"));
    }

    private static int AsInt(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) && value is not null and not DBNull
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;

    private static string AsText(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) && value is not null and not DBNull
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            : "";
}

/* ══════════════════════════════════════════════════════════════════════════
   每日推送提醒
   ──────────────────────────────────────────────────────────────────────────
   用户绑一个渠道（Bark / ntfy / 通用 webhook），PushScheduler 每 30 秒 tick 一次，
   到点就发一条「该练了」。为什么只能做在服务端：页面一关就没有 JS，而 Web Push 需要
   HTTPS 公网 + VAPID 密钥，本项目是 127.0.0.1 反代、SW 只做静态缓存，不划算。

   数据放哪：userdata/push/<user>.json（**子目录**）。三条理由：
     1. 用户数据整包 userdata/<user>.json 由**客户端独占整包覆盖写**，服务端要动它
        必须走 revision/If-Match，每次推送前写一次就是主动制造 409 冲突；
     2. lastSentDate / failCount 是**服务端状态**，天然不属于客户端数据；
     3. webhook token 不该进「导出我的数据」的包。
   为什么必须是子目录：/api/admin/users（Program.cs:356）和 /api/admin/data/status
   （:2486）都按**顶层** *.json 枚举账号，配置文件放顶层会凭空多出一个账号。
   ══════════════════════════════════════════════════════════════════════════ */
