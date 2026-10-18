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
/// SQLite **在线备份**：拿一份一致的库快照，替代 <c>File.Copy(库文件)</c>。
///
/// 为什么必须用它：直接复制活动库的字节，(a) 若源库在 WAL 模式下，<c>-wal</c> 里还没
/// checkpoint 的已提交事务会**漏掉**；(b) 复制期间源库还在被写，拷出来的可能是半截页面。
/// BackupDatabase 走 SQLite 自己的读语义，源库可以继续被读写。
///
/// ⚠️ 它**不是**停写/一致性机制：写操作的互斥仍然由 <see cref="BankWriteGate"/> 负责，
/// 备份只是"不把库拷坏"。所以别拿 <c>SqliteConnection.ClearAllPools()</c> 当停写手段
/// （它只是把空闲池连接关掉，正在服务请求的连接不受影响）。
/// </summary>
internal static class SqliteSnapshot
{
    /// <summary>把 <paramref name="sourcePath"/> 的一致性快照写到 <paramref name="destPath"/>（覆盖已有文件）。</summary>
    public static void Copy(string sourcePath, string destPath)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("题库数据库不存在", source);
        var dest = Path.GetFullPath(destPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
        if (File.Exists(dest)) File.Delete(dest);
        using (var from = Open(source, SqliteOpenMode.ReadOnly))
        using (var to = Open(dest, SqliteOpenMode.ReadWriteCreate))
        {
            from.BackupDatabase(to);
        }
    }

    /// <summary>
    /// 备份连接一律不进连接池：池里的空闲连接在 Windows 上仍占着文件句柄，
    /// 会让随后对备份目录/版本目录的 rename、Delete 失败。
    /// </summary>
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            DefaultTimeout = 30,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }
}
