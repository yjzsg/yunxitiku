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

sealed class UserDataStore
{
    private readonly AppPaths _paths;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public UserDataStore(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// 取某个用户的**串行锁**。普通 LoadAsync/SaveAsync 内部走同一张表，
    /// 所以只要管理员的写操作也调这里，就会和用户的 load/save 排到同一条队上。
    ///
    /// 为什么必须有这个出口：管理员链路（导入 / 清空 / 删号）以前直接 File.*，
    /// 完全不经过这把锁。于是会出现这样的交错 ——
    ///   用户 load 出旧内容(rev A) → 管理员把文件换成新内容(rev B) → 用户 save(基于 rev A)
    /// 用户那次 save 因为 rev 对不上而撞冲突、被拒（UserDataConflictException），
    /// 但它发生在管理员写完之后，语义上"用户读到的东西"和"他覆盖的东西"已经不是同一份了；
    /// 真正危险的是**管理员清空/删号**：它不带 revision 校验，一旦和用户的写交错，
    /// 就会出现"用户刚保存的进度被清空"或"已删除的账号被一次保存复活"。
    /// 把管理员的写也纳进同一把锁，这类交错就变成确定性的前后关系。
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(string user, Func<Task<T>> work)
    {
        var clean = CleanUserName(user);
        var gate = _locks.GetOrAdd(clean, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>同步版（导入/清空/删号都是文件系统操作，用不上 async 的等待点）。</summary>
    public T RunExclusive<T>(string user, Func<T> work)
    {
        var clean = CleanUserName(user);
        var gate = _locks.GetOrAdd(clean, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            return work();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 管理员的"清空某用户数据"：写回空对象。**在用户的锁内**执行，
    /// 避免和该用户的并发 save 交错（旧实现直接 File.WriteAllText，不带锁）。
    /// </summary>
    public string Clear(string user)
    {
        return RunExclusive(user, () =>
        {
            Directory.CreateDirectory(_paths.UserDataRoot);
            var path = UserDataPath(_paths, user);
            var body = "{}";
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, body, new UTF8Encoding(false));
                RestrictFilePermissions(temp);
                File.Move(temp, path, true);
                RestrictFilePermissions(path);
            }
            finally
            {
                TryDeleteFile(temp);
            }
            return Revision(body);
        });
    }

    /// <summary>
    /// 管理员的"删除某用户数据"：**在用户的锁内**执行。
    /// 必须持锁的原因：删号是"读 auth + 删文件"两步，若与用户 save 交错，
    /// 用户那次 save 会在删除之后把文件写回来（"已删除的账号被保存复活"）。
    /// </summary>
    public void Delete(string user)
    {
        RunExclusive(user, () =>
        {
            var path = UserDataPath(_paths, user);
            if (File.Exists(path)) File.Delete(path);
            return true;
        });
    }

    public async Task<(object Data, string Revision)> LoadAsync(string user)
    {
        var clean = CleanUserName(user);
        var gate = _locks.GetOrAdd(clean, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var path = UserDataPath(_paths, clean);
            string body;
            try
            {
                body = await File.ReadAllTextAsync(path, Encoding.UTF8);
            }
            catch (FileNotFoundException)
            {
                // 首次用户：文件还没建立，这是**正常**的空状态
                return (new Dictionary<string, object?>(), "");
            }
            catch (DirectoryNotFoundException)
            {
                return (new Dictionary<string, object?>(), "");
            }
            /* ⚠️ 除「文件不存在」以外的异常（共享冲突 / 权限 / IO）**必须继续上抛**。
               旧实现先 File.Exists 再读，读失败被外层 catch 吞成「和首次用户一样」→
               路由返回 200 + 空包 → 前端把空包当基线 → 下一次保存整包覆盖真实进度。
               抛出去让路由层回 5xx，前端才有机会进只读态。 */
            if (body.Trim().Length == 0) return (new Dictionary<string, object?>(), "");
            var data = JsonSerializer.Deserialize<object>(body) ?? new Dictionary<string, object?>();
            return (data, Revision(body));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> SaveAsync(string user, string body, string expectedRevision, bool expectEmpty = false)
    {
        var clean = CleanUserName(user);
        var gate = _locks.GetOrAdd(clean, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_paths.UserDataRoot);
            var path = UserDataPath(_paths, clean);
            var currentBody = File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8) : "";
            var currentRevision = currentBody.Length == 0 ? "" : Revision(currentBody);
            if (expectEmpty)
            {
                // 客户端说「我这边没有数据」，可服务端已经有内容 → 冲突，绝不覆盖
                if (currentRevision.Length > 0) throw new UserDataConflictException();
            }
            else if (expectedRevision.Length > 0 && !string.Equals(expectedRevision, currentRevision, StringComparison.OrdinalIgnoreCase))
            {
                throw new UserDataConflictException();
            }

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, body, new UTF8Encoding(false));
                File.Move(temp, path, true);
                RestrictFilePermissions(path);
            }
            finally
            {
                TryDeleteFile(temp);
            }
            return Revision(body);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Revision(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
