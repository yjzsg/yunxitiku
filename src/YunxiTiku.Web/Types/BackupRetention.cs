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

static class BackupRetention
{
    public static void Prune(string root, int keepLatest = 20, int keepDays = 30)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, keepDays));
            var entries = Directory.EnumerateFileSystemEntries(root)
                .Select(path => new { Path = path, Updated = File.GetLastWriteTimeUtc(path) })
                .OrderByDescending(item => item.Updated)
                .ToList();
            // 先按数量硬截断。旧写法是"排 20 名之外**且**超过 30 天"才删，
            // 结果当天创建的备份永远不删（线上 _backups 已涨到 1.2GB）。
            foreach (var item in entries.Skip(Math.Max(1, keepLatest))) DeleteEntry(item.Path);
            // 再叠加"太旧就删"，即使还在前 N 名里
            foreach (var item in entries.Take(Math.Max(1, keepLatest)).Where(item => item.Updated < cutoff)) DeleteEntry(item.Path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 备份清理失败: {ex.Message}");
        }
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else if (File.Exists(path)) File.Delete(path);
    }
}
