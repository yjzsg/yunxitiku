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

internal static class UserDataEndpoints
{
    public static void MapUserData(this WebApplication app)
    {
    app.MapGet("/api/user/load", async (HttpContext context, string? user, UserDataStore userData) =>
    {
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        try
        {
            var loaded = await userData.LoadAsync(clean);
            return Results.Json(new { ok = true, user = clean, data = loaded.Data, revision = loaded.Revision });
        }
        catch (Exception ex)
        {
            /* ⚠️ 读失败**绝不能**伪装成「这个账号没有数据」。
               旧实现返回 200 + {data:{},revision:""}，前端就把空包当基线：等读恢复正常，
               用户随手做一步操作，下一次整包保存就把真实进度整包覆盖掉
               （实测 supa.json 2651 → 700 字节，四门课的 answers/notes/verified/dailyActivity 全丢）。
               这里返回 5xx，前端据此进只读态、不写回。 */
            SafeErrorText(ex);   // 只借它把完整堆栈记进服务端日志，细节不外泄
            return Results.Json(new { ok = false, error = "读取用户数据失败" }, statusCode: 500);
        }
    });

    app.MapPost("/api/user/save", async (HttpContext context, string? user, UserDataStore userData) =>
    {
        var request = context.Request;
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        /* ContentLength 是「线上字节数」：带 Content-Encoding: gzip 时它是压缩后的长度，
           所以真正的 32MB 上限要看解压后的实际字节数 —— 读的时候限长，别等 ReadToEndAsync
           把解压炸弹整个吃进内存再判。 */
        if (request.ContentLength > 32L * 1024 * 1024) return Results.Json(new { ok = false, error = "用户数据超过 32MB，请清理历史记录或导出归档" }, statusCode: 400);
        var (body, readError) = await ReadBodyWithLimitAsync(request.Body, 32 * 1024 * 1024, context.RequestAborted);
        if (readError is not null) return Results.Json(new { ok = false, error = readError }, statusCode: 400);
        if (body is null) return Results.Json(new { ok = false, error = "用户数据超过 32MB，请清理历史记录或导出归档" }, statusCode: 400);
        if (string.IsNullOrWhiteSpace(body)) body = "{}";
        // 必须是 JSON 对象（以前接受数组/标量并落盘，导出与备份的消费者都要跟着防）
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Results.Json(new { ok = false, error = "用户数据必须是 JSON 对象" }, statusCode: 400);
            }
        }
        catch (JsonException ex)
        {
            return Results.Json(new { ok = false, error = "用户数据不是有效 JSON：" + ex.Message }, statusCode: 400);
        }
        /* 前置条件必须**显式**，三种写法都收：
             · If-Match: "<revision>"   常规：内容必须与客户端手里的 revision 一致
             · If-None-Match: *         标准 HTTP「只在资源不存在时创建」
             · If-Match: "" / expectEmpty=1 / ?revision=（空值）  「客户端这边没有数据」
           ⚠️ 最后一种以前会被当成「无前置条件」直接写 —— 那正是覆盖用户真实进度的那条路
           （读失败 → revision="" → 下一次保存把 2651 字节整包换成 700 字节）。
           完全不带前置条件（老客户端）行为不变。 */
        var hasIfNoneMatch = request.Headers.TryGetValue("If-None-Match", out var ifNoneMatchValues);
        var hasIfMatch = request.Headers.TryGetValue("If-Match", out var ifMatchValues);
        var ifNoneMatch = ifNoneMatchValues.ToString();
        var ifMatch = ifMatchValues.ToString();
        var expectedRevision = "";
        var expectEmpty = false;
        if (hasIfNoneMatch && ifNoneMatch.Contains('*', StringComparison.Ordinal))
        {
            expectEmpty = true;
        }
        else if (hasIfMatch)
        {
            var tag = ifMatch.Trim();
            if (tag != "*")
            {
                tag = tag.Trim('"').Trim();
                if (tag.Length == 0) expectEmpty = true;      // If-Match: "" = 「我这边是空的」
                else expectedRevision = tag;
            }
        }
        else if (request.Query.ContainsKey("revision"))
        {
            var revisionQuery = (Convert.ToString(request.Query["revision"], CultureInfo.InvariantCulture) ?? "").Trim();
            if (revisionQuery.Length > 0) expectedRevision = revisionQuery;
            else expectEmpty = true;                          // ?revision= 空值 = 「我这边是空的」
        }
        else if (request.Query.ContainsKey("expectEmpty"))
        {
            var flag = (Convert.ToString(request.Query["expectEmpty"], CultureInfo.InvariantCulture) ?? "").Trim();
            expectEmpty = flag is "1" or "true" or "True";
        }
        try
        {
            var revision = await userData.SaveAsync(clean, body, expectedRevision, expectEmpty);
            return Results.Json(new { ok = true, user = clean, revision, savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        }
        catch (UserDataConflictException)
        {
            return Results.Json(new { ok = false, error = "用户数据已在其他页面更新，请刷新后重试", code = "user_data_conflict" }, statusCode: 409);
        }
    });

    app.MapPost("/api/user/patch", async (HttpContext context, string? user, UserDataStore userData) =>
    {
        /* 增量保存：客户端只发「相对服务端那份 revision 变了的部分」。
           实测一次作答 4 条 op、gzip 167 字节，而整包 gzip 是 106KB —— 移动网络下是几百毫秒的差别。
           前置条件与 /api/user/save 完全一致（base 对不上就 409），revision 语义不变，
           所以老客户端不发这个端点也照常工作；新客户端遇到 400/404/405 会退回整包保存。 */
        var request = context.Request;
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        /* 增量请求正常只有几百字节到几十 KB；4MB 上限是防着「客户端算出巨大 diff 还硬发」。 */
        if (request.ContentLength > 4L * 1024 * 1024) return Results.Json(new { ok = false, error = "增量保存请求超过 4MB，请改用整包保存", code = "user_data_patch_rejected" }, statusCode: 400);
        var (body, readError) = await ReadBodyWithLimitAsync(request.Body, 4 * 1024 * 1024, context.RequestAborted);
        if (readError is not null) return Results.Json(new { ok = false, error = readError, code = "user_data_patch_rejected" }, statusCode: 400);
        if (body is null) return Results.Json(new { ok = false, error = "增量保存请求超过 4MB，请改用整包保存", code = "user_data_patch_rejected" }, statusCode: 400);

        string baseRevision;
        List<UserDataPatchOp> ops;
        try { (baseRevision, ops) = UserDataPatch.Parse(body); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message, code = "user_data_patch_rejected" }, statusCode: 400); }
        catch (JsonException ex) { return Results.Json(new { ok = false, error = "增量保存请求不是有效 JSON：" + ex.Message, code = "user_data_patch_rejected" }, statusCode: 400); }

        try
        {
            var (revision, applied) = await userData.PatchAsync(clean, baseRevision, ops);
            return Results.Json(new { ok = true, user = clean, revision, applied, savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        }
        catch (UserDataConflictException)
        {
            return Results.Json(new { ok = false, error = "用户数据已在其他页面更新，请刷新后重试", code = "user_data_conflict" }, statusCode: 409);
        }
        catch (InvalidOperationException ex)
        {
            /* 结构分叉（中间层不是对象等）：拒绝，且一个字节都没写。客户端收到后应退回整包保存。 */
            return Results.Json(new { ok = false, error = ex.Message, code = "user_data_patch_rejected" }, statusCode: 400);
        }
        catch (Exception ex)
        {
            SafeErrorText(ex);
            return Results.Json(new { ok = false, error = "保存用户数据失败" }, statusCode: 500);
        }
    });

    app.MapPost("/api/user/import", async (HttpContext context, string? user, UserDataStore userData) =>
    {
        /* 恢复用户自己导出的备份。
           以前「导出我的数据」导出的 JSON 没有任何回灌路径 —— 全站文件输入只收 .zip（管理员用），
           用户以为存了备份，真出事时恢复不了。这个端点补上另一半。

           与 /api/user/save 的三点不同：
             1. 接受导出包 {exportedAt,user,revision,data}，剥掉外层只落 data（也接受裸 data 对象）；
             2. 默认**不覆盖**已有数据 —— 必须显式带 force=1 才覆盖，否则 409。
                覆盖会丢掉「导出之后新做的题」，这是不可逆的，不能靠一个默认真值决定；
             3. 覆盖时仍然走 If-Match，前端拿的是导入前的 revision，与页面上的编辑并发安全。 */
        var request = context.Request;
        string clean;
        try { clean = ResolveRequestedUser(context, user); }
        catch (InvalidOperationException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400); }
        if (request.ContentLength > 32L * 1024 * 1024)
        {
            return Results.Json(new { ok = false, error = "备份文件超过 32MB" }, statusCode: 400);
        }
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        if (Encoding.UTF8.GetByteCount(body) > 32 * 1024 * 1024)
        {
            return Results.Json(new { ok = false, error = "备份文件超过 32MB" }, statusCode: 400);
        }
        if (string.IsNullOrWhiteSpace(body)) return Results.Json(new { ok = false, error = "备份文件是空的" }, statusCode: 400);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex) { return Results.Json(new { ok = false, error = "备份文件不是有效 JSON：" + ex.Message }, statusCode: 400); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Results.Json(new { ok = false, error = "备份文件必须是 JSON 对象" }, statusCode: 400);

            JsonElement data;
            string exportedFrom = "";
            if (root.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                data = inner;
                if (root.TryGetProperty("user", out var fromUser) && fromUser.ValueKind == JsonValueKind.String)
                {
                    exportedFrom = (fromUser.GetString() ?? "").Trim();
                }
            }
            else
            {
                // 裸 data：管理员 zip 里就是这个形状，也允许直接贴。
                data = root;
            }

            /* 拦一次明显贴错的东西：导出包里一定有 courses（哪怕为空对象）。
               没有就说明这是别的 JSON，落盘只会把用户进度换成垃圾。 */
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("courses", out _))
            {
                return Results.Json(new { ok = false, error = "这不是云习题库的备份文件（缺少 courses 字段）" }, statusCode: 400);
            }

            var force = (Convert.ToString(request.Query["force"], CultureInfo.InvariantCulture) ?? "").Trim() is "1" or "true" or "True";
            string expectedRevision = "";
            if (request.Headers.TryGetValue("If-Match", out var ifMatchValues))
            {
                var tag = ifMatchValues.ToString().Trim();
                if (tag != "*") expectedRevision = tag.Trim('"').Trim();
            }

            // 先看现有数据：没有就直接写；有就要求 force。
            /* ⚠️ 「有没有数据」不能用类型判断：首次用户 LoadAsync 回的是空 Dictionary，
               而落盘读回来的可能是 JsonObject / JsonElement / Dictionary，形状不固定。
               统一按「JSON 对象里有没有键」判，另外把 revision 为空也算作没有数据。 */
            var existing = await userData.LoadAsync(clean);
            var hasExisting = existing.Revision.Length > 0 && HasAnyProperty(existing.Data);
            if (hasExisting && !force)
            {
                return Results.Json(new
                {
                    ok = false,
                    code = "user_data_exists",
                    error = exportedFrom.Length > 0 && !exportedFrom.Equals(clean, StringComparison.OrdinalIgnoreCase)
                        ? $"这个备份来自账号「{exportedFrom}」，导入会把「{clean}」现有的进度整体替换掉"
                        : "导入会把这个账号现有的进度整体替换掉",
                    revision = existing.Revision,
                    exportedFrom,
                }, statusCode: 409);
            }

            try
            {
                var revision = await userData.SaveAsync(clean, data.GetRawText(), expectedRevision, expectEmpty: false);
                return Results.Json(new { ok = true, user = clean, revision, importedFrom = exportedFrom });
            }
            catch (UserDataConflictException)
            {
                return Results.Json(new { ok = false, error = "用户数据已在其他页面更新，请刷新后重试", code = "user_data_conflict" }, statusCode: 409);
            }
        }
    });
    }
}
