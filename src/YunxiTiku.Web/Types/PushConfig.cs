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

sealed class PushConfig
{
    public bool Enabled { get; set; }
    public string Time { get; set; } = "20:00";
    public int GraceHours { get; set; } = 6;
    public string Style { get; set; } = "milestone";   // remind | milestone | digest
    public string BaseUrl { get; set; } = "";
    public PushChannelConfig Channel { get; set; } = new();

    /* 以下全是**服务端状态**：PushConfigStore.Save 只白名单合并用户可编辑字段，
       客户端改不动它们。否则前端一次误传就能清掉 lastSentDate，同一天重复推送。 */
    public string LastSentDate { get; set; } = "";
    public string LastSentAt { get; set; } = "";
    public string LastResult { get; set; } = "";
    public string LastError { get; set; } = "";
    public int FailCount { get; set; }
    public string NextRetryAt { get; set; } = "";
    /// <summary>上次已经发过的连续天数里程碑。断签（streak 变小）时归零重来。</summary>
    public int MilestoneStreak { get; set; }
}
