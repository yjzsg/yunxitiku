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

/// <summary>浏览器推送订阅（PushSubscription.toJSON() 的形状）。</summary>
sealed class PushSubscription
{
    public string Endpoint { get; set; } = "";
    /// <summary>P-256 公钥（base64url，未压缩点，65 字节）。</summary>
    public string P256dh { get; set; } = "";
    /// <summary>16 字节认证密钥（base64url）。</summary>
    public string Auth { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string CreatedAt { get; set; } = "";

    public bool IsValid =>
        Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && P256dh.Length > 0 && Auth.Length > 0;
}
