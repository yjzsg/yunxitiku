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
/// 账号库（accounts.dat）不可用：文件存在但读不出来 / 解析失败 / 结构不对。
///
/// 独立成一个类型，是为了让它和普通 InvalidOperationException 走**不同的对外文案**：
/// 这类异常的 Message 里含绝对路径和解析器细节，而它的触发点可能是**未登录请求**
/// （/api/auth/session 一进来就要解析 AuthStore），不能原样回显给调用方。
/// 完整文案仍然写进服务端日志（见 SafeErrorText）。
/// </summary>
sealed class AuthStoreUnavailableException : Exception
{
    public AuthStoreUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
