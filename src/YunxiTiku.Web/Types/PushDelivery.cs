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

/// <summary>「是不是 webpush」这一个分支收在一处：调度器和「发送测试」共用。</summary>
static class PushDelivery
{
    public static Task<(bool Ok, string Error)> SendAsync(WebPushSender webPush, string user, PushChannelConfig channel, PushMessage message, CancellationToken ct)
    {
        var type = (channel?.Type ?? "").Trim().ToLowerInvariant();
        return type == "webpush"
            ? webPush.SendAsync(user, channel!, message, ct)
            : PushSenders.SendAsync(channel!, message, ct);
    }
}
