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

/// <summary>服务端从用户数据里算出来的「今日摘要」。全部只读，不回写用户数据。</summary>
sealed class PushDigest
{
    public int Due { get; set; }
    public int WrongTotal { get; set; }
    public int DoneToday { get; set; }
    public int CorrectToday { get; set; }
    public int VerifiedToday { get; set; }
    public int Streak { get; set; }
    /// <summary>距最近一场考试的天数；-1 = 没有设置考试日期。</summary>
    public int DaysLeft { get; set; } = -1;
    public int DailyTarget { get; set; }
    public string CourseName { get; set; } = "";
    /// <summary>距上一次真正练过的天数；-1 = 从来没练过。</summary>
    public int DaysSinceLast { get; set; } = -1;
}
