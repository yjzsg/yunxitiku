using System.Net;
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
/// Minimal, cross-platform SOAP 1.1 client for the JinKaodian exam web service.
/// No WCF / no generated proxy / no client DLLs: a hand-built envelope over HttpClient.
/// Requires the MGSoapHeader auth header - without it the server silently returns
/// empty datasets with HTTP 200 (that is the whole reason BankUpdater.exe never worked).
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

        Exception? last = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await SendAsync(method, envelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                last = ex;
                RetryCount++;
                await Task.Delay(1500 * attempt, ct).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException($"{method}: SOAP call failed");
    }

    private async Task<SoapResult> SendAsync(string method, string envelope, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ServiceUrl);
        var content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(envelope));
        content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
        req.Content = content;
        req.Headers.TryAddWithoutValidation("SOAPAction", "\"http://tempuri.org/" + method + "\"");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        CallCount++;

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{method}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} - {Head(body, 300)}");
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(body);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{method}: response is not XML - {ex.Message}; head={Head(body, 200)}");
        }

        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault != null)
        {
            var code = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultcode")?.Value;
            var str = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
            throw new InvalidOperationException($"{method}: SOAP fault {code} - {str}");
        }

        var responseElem = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == method + "Response");
        var resultElem = responseElem?.Elements().FirstOrDefault(e => e.Name.LocalName == method + "Result")
                         ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == method + "Result");

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
            Primary = resultElem?.Value,
            Extras = extras,
            RawLength = body.Length,
        };
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or IOException ||
        ex.InnerException is HttpRequestException;

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    public void Dispose() => _http.Dispose();
}
