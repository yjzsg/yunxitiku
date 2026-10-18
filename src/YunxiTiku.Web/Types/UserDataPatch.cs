using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// 增量保存（/api/user/patch）的一个操作。
/// Path 是 JSON 指针风格的分段路径（"courses/20/answers/18138754"），Kind 取 set / del / app。
/// Value 用 JsonNode 保存，null 表示**JSON 的 null**（set 一个 null 值是合法的）。
/// </summary>
sealed record UserDataPatchOp(string[] Path, string Kind, JsonNode? Value);

/// <summary>
/// 增量保存的解析与合并。
///
/// 为什么要这条路径：用户数据实测约 1MB（叶九洲.json 1,006,956 字节，gzip 后 106KB），
/// 而「答一道题」真正变的内容只有几百字节。整包上传在移动网络下是一趟几百毫秒的等待，
/// 增量后同一操作是几十字节 —— 实测 ops 4 条、gzip 167 字节。
///
/// 语义刻意保持和整包保存一致：revision 仍是「落盘全文的 SHA256」，
/// 所以新旧客户端、草稿、冲突判定全都不受影响。
/// </summary>
static class UserDataPatch
{
    /// <summary>一次最多多少个操作。正常作答是 3~10 条；到这个量级说明该走整包了。</summary>
    public const int MaxOps = 2000;

    /* 落盘序列化必须和客户端 JSON.stringify 尽量一致：不转义非 ASCII（笔记里全是中文，
       默认编码器会把每个汉字变成 \\uXXXX，文件体积翻几倍），不缩进，键序保持原样。 */
    static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static (string BaseRevision, List<UserDataPatchOp> Ops) Parse(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("增量保存请求不是有效 JSON：" + ex.Message);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("增量保存请求必须是 JSON 对象");
            var baseRevision = root.TryGetProperty("base", out var baseEl) && baseEl.ValueKind == JsonValueKind.String
                ? (baseEl.GetString() ?? "").Trim()
                : "";
            /* base 必须显式给：没有它就等于「无条件覆盖」，而增量保存的合并基准正是它。 */
            if (baseRevision.Length == 0) throw new InvalidOperationException("增量保存必须带 base（客户端手里的 revision）");
            if (!root.TryGetProperty("ops", out var opsEl) || opsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("增量保存缺少 ops 数组");
            }
            var count = opsEl.GetArrayLength();
            if (count > MaxOps) throw new InvalidOperationException($"一次最多 {MaxOps} 个操作，请改用整包保存");

            var ops = new List<UserDataPatchOp>(count);
            foreach (var item in opsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("ops 的每一项必须是对象");
                var rawPath = item.TryGetProperty("p", out var pEl) && pEl.ValueKind == JsonValueKind.String ? pEl.GetString() : null;
                if (string.IsNullOrEmpty(rawPath) || rawPath[0] != '/') throw new InvalidOperationException("ops 的 p 必须以 / 开头");
                var segments = rawPath.Split('/');
                /* segments[0] 是空串（路径以 / 开头），后面每一段都必须非空 */
                if (segments.Length < 2) throw new InvalidOperationException("ops 的 p 不能是根");
                for (var i = 1; i < segments.Length; i++)
                {
                    if (segments[i].Length == 0) throw new InvalidOperationException($"ops 的 p 含有空段：{rawPath}");
                }
                var path = segments[1..];

                if (item.TryGetProperty("v", out var vEl))
                {
                    /* 用原始文本重建，保证数字/转义与客户端发来的一字不差 */
                    ops.Add(new UserDataPatchOp(path, "set", JsonNode.Parse(vEl.GetRawText())));
                }
                else if (item.TryGetProperty("d", out var dEl) && dEl.ValueKind is JsonValueKind.True or JsonValueKind.Null or JsonValueKind.Number)
                {
                    ops.Add(new UserDataPatchOp(path, "del", null));
                }
                else if (item.TryGetProperty("app", out var appEl) && appEl.ValueKind == JsonValueKind.Array)
                {
                    ops.Add(new UserDataPatchOp(path, "app", JsonNode.Parse(appEl.GetRawText())));
                }
                else
                {
                    throw new InvalidOperationException($"ops 的 {rawPath} 缺少 v / d / app");
                }
            }
            return (baseRevision, ops);
        }
    }

    /// <summary>把 ops 应用到当前落盘内容上，返回合并后的全文。当前内容为空时按 {} 处理。</summary>
    public static string Apply(string currentBody, IReadOnlyList<UserDataPatchOp> ops)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(currentBody)) root = new JsonObject();
        else
        {
            root = JsonNode.Parse(currentBody) as JsonObject
                ?? throw new InvalidOperationException("服务端当前用户数据不是 JSON 对象，无法增量合并");
        }

        foreach (var op in ops)
        {
            var node = root;
            for (var i = 0; i < op.Path.Length - 1; i++)
            {
                var seg = op.Path[i];
                if (node[seg] is JsonObject child) { node = child; continue; }
                /* 中间层不存在就建出来；存在但不是对象说明客户端与服务端的结构认知已经分叉，
                   这时**宁可失败**也不能瞎猜 —— 猜错就是丢用户数据。 */
                if (node[seg] is not null) throw new InvalidOperationException($"增量路径 /{string.Join('/', op.Path)} 的中间层不是对象");
                child = new JsonObject();
                node[seg] = child;
                node = child;
            }
            var last = op.Path[^1];
            switch (op.Kind)
            {
                case "set":
                    node[last] = op.Value?.DeepClone();
                    break;
                case "del":
                    node.Remove(last);
                    break;
                case "app":
                    if (node[last] is not JsonArray arr)
                    {
                        arr = new JsonArray();
                        node[last] = arr;
                    }
                    if (op.Value is JsonArray items)
                    {
                        foreach (var item in items) arr.Add(item?.DeepClone());
                    }
                    break;
                default:
                    throw new InvalidOperationException("未知的 op 类型：" + op.Kind);
            }
        }
        return root.ToJsonString(WriteOptions);
    }
}
