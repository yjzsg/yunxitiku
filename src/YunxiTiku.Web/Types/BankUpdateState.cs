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
/// 一次更新检查的结论。**三态**，别把 Unknown 混进 Unchanged：
/// 只有 <see cref="Unchanged"/> 才允许跳过拉取，Unknown 一律照拉。
/// </summary>
enum BankUpdateState
{
    /// <summary>确定上游有更新（章节或题目任一有增量）。</summary>
    Updated,

    /// <summary>确定没有更新（两个接口都通、都解析成 0 行）。</summary>
    Unchanged,

    /// <summary>拿不到结论（网络超时 / SOAP 错误 / 返回格式变了）。</summary>
    Unknown,
}

/// <summary>单门课的检查结果。</summary>
readonly record struct BankUpdateCheck(BankUpdateState State, int Chapters, int Subjects, string? Error)
{
    /// <summary>守卫用：**只在确定无更新时**才跳过拉取。</summary>
    public bool IsCertainlyUnchanged => State == BankUpdateState.Unchanged;

    public static BankUpdateCheck Updated(int chapters, int subjects) => new(BankUpdateState.Updated, chapters, subjects, null);
    public static BankUpdateCheck Unchanged() => new(BankUpdateState.Unchanged, 0, 0, null);
    public static BankUpdateCheck Unknown(string error) => new(BankUpdateState.Unknown, 0, 0, error);
}
