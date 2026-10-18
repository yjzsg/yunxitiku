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

sealed class PushChannelConfig
{
    /// <summary>bark | ntfy | webhook | wechat | serverchan | pushplus | webpush</summary>
    public string Type { get; set; } = "webpush";
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>仅 webhook 用：正文模板，占位符 {{title}} {{body}} {{url}}（会做 JSON 转义）。</summary>
    public string Template { get; set; } = "";
    /// <summary>
    /// 仅 webpush 用：绑定的设备。一个账号可以绑多台（手机 + 电脑），
    /// 发送时逐台推，全部失败才算失败。
    /// 这一项**客户端改不动**（Save 会保留原值），只能通过 /api/push/subscribe 增删。
    /// </summary>
    public List<PushSubscription> Subscriptions { get; set; } = new();
}
