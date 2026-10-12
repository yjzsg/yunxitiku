using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace JkdWeb.BankPuller;

/// <summary>
/// Result of one SOAP call: the text of &lt;Op&gt;Result plus any sibling ref/out
/// parameters (e.g. lastUpdateDate) returned by the ASMX method.
/// </summary>
public sealed class SoapResult
{
    public string? Primary { get; init; }
    public Dictionary<string, string?> Extras { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int RawLength { get; init; }
    public string? GetExtra(string name) => Extras.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// 一次 SOAP 调用失败的类别。调用方按类别决定「重试 / 让整次拉取失败 / 记为单题坏题」：
/// <list type="bullet">
/// <item><see cref="Network"/>：连接失败、超时（deadline）、网关 5xx / 限流 —— 暂态，自动重试；
///       重试耗尽后**必须让整次拉取失败**，绝不能摊成"坏题"。</item>
/// <item><see cref="Auth"/>：HTTP 401/403 或鉴权类 SOAP fault —— 重试没有意义，也绝不能降级成"坏题"。</item>
/// <item><see cref="Protocol"/>：响应不是约定的 SOAP/DataTable 形状（非 XML、4xx、非鉴权 fault）——
///       契约层错误，同样必须让整次拉取失败。</item>
/// <item><see cref="Upstream"/>：HTTP 500（上游应用内部错误）—— 唯一"可能只是这一批里某一行坏"
///       的类别：调用方可以二分定位；缩小到单题仍失败，才是该题的数据缺陷。</item>
/// </list>
/// </summary>
public enum SoapFailureKind
{
    Network,
    Auth,
    Protocol,
    Upstream,
}

/// <summary>
/// SOAP 调用失败，已分类。类别是给调用方看的契约：<see cref="Kind"/> 为
/// <see cref="SoapFailureKind.Upstream"/> 时调用方才可以把它当成"这批数据有问题"；
/// 其余类别与题目内容无关，只允许让整次拉取失败。
/// </summary>
public class SoapCallException : Exception
{
    public SoapCallException(SoapFailureKind kind, string method, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Method = method;
    }

    public SoapFailureKind Kind { get; }
    public string Method { get; }
    public int? HttpStatus { get; init; }

    /// <summary>暂态：值得原样重试（网络故障、deadline、5xx）。鉴权/协议错误重试没有意义。</summary>
    public bool IsTransient => Kind is SoapFailureKind.Network or SoapFailureKind.Upstream;
}

/// <summary>
/// Minimal, cross-platform SOAP 1.1 client for the JinKaodian exam web service.
/// No WCF / no generated proxy / no client DLLs: a hand-built envelope over HttpClient.
/// Requires the MGSoapHeader auth header - without it the server silently returns
/// empty datasets with HTTP 200 (that is the whole reason BankUpdater.exe never worked).
///
/// 失败一律以 **已分类** 的 <see cref="SoapCallException"/> 抛出（用户取消除外，见
/// <see cref="InvokeAsync"/>）：调用方据此区分网络故障 / 鉴权 / 协议 / 单题数据。
///
/// 账号不在这里：见 <see cref="BankCredentials"/>（显式配置或环境变量）。
/// </summary>
public sealed class SoapClient : IDisposable
{
    /// <summary>金考典官方题库服务地址（非机密，客户端本来就用它）。</summary>
    public const string DefaultServiceUrl = "http://app1.jinkaodian.com/CL.ExamWebService/ExamClientService.asmx";

    private readonly HttpClient _http;

    public string ServiceUrl { get; }
    public string UserId { get; }
    public string Password { get; }
    public int CallCount { get; private set; }
    public int RetryCount { get; private set; }
    public int MaxAttempts { get; set; } = 4;

    /// <summary>重试的基础退避毫秒数：第 n 次重试前等 n × 该值（0 = 不退避，测试用）。</summary>
    public int RetryDelayMs { get; set; } = 1500;

    public SoapClient(string serviceUrl, string userId, string password, int timeoutMs = 180000)
    {
        ServiceUrl = serviceUrl;
        UserId = userId;
        Password = password;

        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            AutomaticDecompression = DecompressionMethods.None,
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/4.0 (compatible; MSIE 6.0; MS Web Services Client Protocol 4.0.30319.42000)");
    }

    /// <summary>
    /// 发一次 SOAP 调用。取消语义：调用方取消（<paramref name="ct"/> 触发）时原样抛
    /// <see cref="OperationCanceledException"/> —— 不重试、不包装成别的异常类型，
    /// 否则上一级会把"用户取消"当成一次普通失败（历史上还会被摊成坏题后照常提交）。
    /// </summary>
    public async Task<SoapResult> InvokeAsync(string method, string argsXml, CancellationToken ct = default)
    {
        var envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
            "<soap:Header><MGSoapHeader xmlns=\"http://tempuri.org/\">" +
            "<UserID>" + XmlEscape(UserId) + "</UserID>" +
            "<Password>" + XmlEscape(Password) + "</Password>" +
            "</MGSoapHeader></soap:Header>" +
            "<soap:Body><" + method + " xmlns=\"http://tempuri.org/\">" + argsXml +
            "</" + method + "></soap:Body></soap:Envelope>";

        var attempts = Math.Max(1, MaxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendAsync(method, envelope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 用户取消：立即透传，语义不能被重试/包装抹掉
            }
            catch (SoapCallException ex)
            {
                if (!ex.IsTransient || attempt >= attempts) throw;
                RetryCount++;
                // 退避期间被取消同样立即透传（Task.Delay 会抛 OperationCanceledException）
                await Task.Delay(RetryDelayMs * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<SoapResult> SendAsync(string method, string envelope, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ServiceUrl);
            var content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(envelope));
            content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
            req.Content = content;
            req.Headers.TryAddWithoutValidation("SOAPAction", "\"http://tempuri.org/" + method + "\"");

            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 用户取消
        }
        catch (OperationCanceledException ex)
        {
            // 取消令牌没被触发却收到取消 → HttpClient.Timeout 到点：这是 deadline，不是用户取消。
            // 归网络类（暂态，可重试），与用户取消严格分开。
            throw new SoapCallException(SoapFailureKind.Network, method,
                $"{method}: 请求超时（deadline {_http.Timeout.TotalSeconds:0}s，非用户取消）", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SoapCallException(SoapFailureKind.Network, method,
                $"{method}: 连接失败（{ServiceUrl}） - {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new SoapCallException(SoapFailureKind.Network, method,
                $"{method}: 网络 IO 失败（{ServiceUrl}） - {ex.Message}", ex);
        }
        catch (SocketException ex)
        {
            throw new SoapCallException(SoapFailureKind.Network, method,
                $"{method}: 套接字错误（{ServiceUrl}） - {ex.Message}", ex);
        }

        using (resp)
        {
            string body;
            try
            {
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SoapCallException(SoapFailureKind.Network, method,
                    $"{method}: 读取响应失败 - {ex.Message}", ex);
            }
            CallCount++;

            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var kind = ClassifyStatus(status);
                throw new SoapCallException(kind, method,
                    $"{method}: HTTP {status} {resp.ReasonPhrase}（{DescribeKind(kind)}） - {Head(body, 300)}")
                {
                    HttpStatus = status,
                };
            }

            XDocument doc;
            try
            {
                doc = XDocument.Parse(body);
            }
            catch (Exception ex)
            {
                throw new SoapCallException(SoapFailureKind.Protocol, method,
                    $"{method}: 响应不是 XML（HTML 错误页/网关页？） - {ex.Message}; head={Head(body, 200)}", ex);
            }

            var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault != null)
            {
                var code = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultcode")?.Value;
                var str = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
                var kind = LooksLikeAuthFault(code, str) ? SoapFailureKind.Auth : SoapFailureKind.Protocol;
                throw new SoapCallException(kind, method, $"{method}: SOAP fault {code} - {str}");
            }

            var responseElem = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == method + "Response");
            var resultElem = responseElem?.Elements().FirstOrDefault(e => e.Name.LocalName == method + "Result")
                             ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == method + "Result");

            if (resultElem is null)
            {
                // 形状不对就不能当成"空数据集"：MGSoapHeader 缺失时服务端会静默返回空响应，
                // 继续走下去会把整门课当"上游没数据"从而删掉旧内容。
                throw new SoapCallException(SoapFailureKind.Protocol, method,
                    $"{method}: 响应里没有 <{method}Result>（契约不符，或服务端静默拒绝了鉴权头）; head={Head(body, 200)}");
            }

            var extras = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (responseElem != null)
            {
                foreach (var el in responseElem.Elements())
                {
                    if (el.Name.LocalName == method + "Result") continue;
                    extras[el.Name.LocalName] = el.Value;
                }
            }

            return new SoapResult
            {
                Primary = resultElem.Value,
                Extras = extras,
                RawLength = body.Length,
            };
        }
    }

    /// <summary>
    /// HTTP 状态码 → 失败类别。
    /// 500 = 上游应用内部错误（历史上个别坏题就是它，允许调用方二分定位到单题）；
    /// 401/403 = 鉴权；408/429/其它 5xx = 网络/可用性（暂态）；其余 4xx = 协议。
    /// </summary>
    private static SoapFailureKind ClassifyStatus(int status)
    {
        if (status is 401 or 403) return SoapFailureKind.Auth;
        if (status == 500) return SoapFailureKind.Upstream;
        if (status >= 500 || status is 408 or 429) return SoapFailureKind.Network;
        return SoapFailureKind.Protocol;
    }

    private static string DescribeKind(SoapFailureKind kind) => kind switch
    {
        SoapFailureKind.Auth => "上游拒绝鉴权（检查账号/口令/服务地址）",
        SoapFailureKind.Protocol => "非预期状态码，协议/契约错误",
        SoapFailureKind.Upstream => "上游服务器内部错误（可能是本批数据触发）",
        _ => "上游不可用/限流，暂态",
    };

    /// <summary>SOAP fault 里出现这些词就按鉴权失败处理（而不是当成可二分的数据错误）。</summary>
    private static bool LooksLikeAuthFault(string? code, string? text)
    {
        var hay = ((code ?? "") + " " + (text ?? "")).ToLowerInvariant();
        return hay.Contains("password") || hay.Contains("userid") || hay.Contains("auth") ||
               hay.Contains("login") || hay.Contains("denied") || hay.Contains("forbidden") ||
               hay.Contains("密码") || hay.Contains("账号") || hay.Contains("用户名") ||
               hay.Contains("鉴权") || hay.Contains("认证");
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    public void Dispose() => _http.Dispose();
}
