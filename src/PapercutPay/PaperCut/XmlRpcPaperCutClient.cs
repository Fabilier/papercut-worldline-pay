using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace PapercutPay.PaperCut;

/// <summary>Minimal PaperCut MF Web Services client over XML-RPC (POST {BaseUrl}/rpc/api/xmlrpc).</summary>
public sealed class XmlRpcPaperCutClient(HttpClient http, IOptions<PaperCutOptions> options) : IPaperCutClient
{
    private readonly PaperCutOptions _opt = options.Value;

    public async Task<bool> UserExistsAsync(string userName, CancellationToken ct)
    {
        var value = await CallAsync("api.isUserExists", [Str(_opt.AuthToken), Str(userName)], ct);
        return value.Element("boolean")?.Value.Trim() is "1" or "true";
    }

    public async Task<decimal> GetBalanceAsync(string userName, CancellationToken ct)
    {
        List<XElement> args = [Str(_opt.AuthToken), Str(userName)];
        if (!string.IsNullOrWhiteSpace(_opt.AccountName))
        {
            args.Add(Str(_opt.AccountName));
        }

        var value = await CallAsync("api.getUserAccountBalance", args, ct);
        return ParseDouble(value);
    }

    public async Task AdjustBalanceAsync(string userName, decimal amount, string comment, CancellationToken ct)
    {
        List<XElement> args = [Str(_opt.AuthToken), Str(userName), Dbl(amount), Str(comment)];
        if (!string.IsNullOrWhiteSpace(_opt.AccountName))
        {
            args.Add(Str(_opt.AccountName));
        }

        var result = await CallAsync("api.adjustUserAccountBalance", args, ct);
        EnsureSuccess(result);
    }

    /// <summary>The API returns a boolean (1 = success). Anything else is not treated as a credit.</summary>
    internal static void EnsureSuccess(XElement value)
    {
        switch (value.Element("boolean")?.Value.Trim())
        {
            case "1" or "true":
                return;
            case "0" or "false":
                throw new PaperCutNotAppliedException("PaperCut answered false to adjustUserAccountBalance.");
            default:
                throw new PaperCutException("Unexpected answer to adjustUserAccountBalance; outcome unknown.");
        }
    }

    internal static string BuildRequest(string method, IEnumerable<XElement> values) =>
        new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("methodCall",
                new XElement("methodName", method),
                new XElement("params", values.Select(v => new XElement("param", new XElement("value", v)))))
        ).ToString(SaveOptions.DisableFormatting);

    private static XElement Str(string s) => new("string", s);

    private static XElement Dbl(decimal d) => new("double", d.ToString("F2", CultureInfo.InvariantCulture));

    private static decimal ParseDouble(XElement value) =>
        decimal.Parse(
            value.Element("double")?.Value ?? value.Element("string")?.Value
                ?? throw new PaperCutException("Unexpected XML-RPC value type."),
            CultureInfo.InvariantCulture);

    private async Task<XElement> CallAsync(string method, IEnumerable<XElement> args, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl.TrimEnd('/')}/rpc/api/xmlrpc")
        {
            Content = new StringContent(BuildRequest(method, args), Encoding.UTF8, new MediaTypeHeaderValue("text/xml")),
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            // No response received: the request did not reach PaperCut or was refused.
            throw new PaperCutNotAppliedException($"PaperCut unreachable calling {method}.", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new PaperCutException($"Timeout calling {method}; outcome unknown.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new PaperCutNotAppliedException($"PaperCut returned HTTP {(int)response.StatusCode} for {method}.");
            }

            XDocument doc;
            try
            {
                using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                doc = XDocument.Load(reader);
            }
            catch (XmlException ex)
            {
                throw new PaperCutException($"Unreadable answer to {method}; outcome unknown.", ex);
            }

            var fault = doc.Root?.Element("fault");
            if (fault is not null)
            {
                throw new PaperCutNotAppliedException($"PaperCut fault for {method}: {fault.Value.Trim()}");
            }

            return doc.Root?.Element("params")?.Element("param")?.Element("value") ?? new XElement("value");
        }
    }
}
