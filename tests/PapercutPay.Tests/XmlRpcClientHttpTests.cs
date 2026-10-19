using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PapercutPay.PaperCut;

namespace PapercutPay.Tests;

public class XmlRpcClientHttpTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Xml(string valueXml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"<?xml version=\"1.0\"?><methodResponse><params><param><value>{valueXml}</value></param></params></methodResponse>",
            Encoding.UTF8, "text/xml"),
    };

    private static XmlRpcPaperCutClient Client(StubHandler h, string account = "") =>
        new(new HttpClient(h), Options.Create(new PaperCutOptions { BaseUrl = "http://pc.local:9191/", AuthToken = "tok", AccountName = account }));

    private static string[] ParamsOf(string body) =>
        XDocument.Parse(body).Root!.Element("params")!.Elements("param")
            .Select(p => p.Element("value")!.Elements().Single().Value).ToArray();

    [Fact]
    public async Task Adjust_posts_to_xmlrpc_endpoint_with_token_user_amount_comment()
    {
        var h = new StubHandler((_, _) => Xml("<boolean>1</boolean>"));

        await Client(h).AdjustBalanceAsync("alice", 12.5m, "ref", default);

        var call = Assert.Single(h.Calls);
        Assert.Equal("http://pc.local:9191/rpc/api/xmlrpc", call.Uri!.ToString());
        Assert.Equal("api.adjustUserAccountBalance", XDocument.Parse(call.Body).Root!.Element("methodName")!.Value);
        Assert.Equal(["tok", "alice", "12.50", "ref"], ParamsOf(call.Body));
    }

    [Fact]
    public async Task Adjust_sends_account_name_as_fifth_parameter_when_configured()
    {
        var h = new StubHandler((_, _) => Xml("<boolean>1</boolean>"));

        await Client(h, "Secondary").AdjustBalanceAsync("alice", 5m, "ref", default);

        Assert.Equal(["tok", "alice", "5.00", "ref", "Secondary"], ParamsOf(h.Calls[0].Body));
    }

    [Fact]
    public async Task Adjust_returning_false_is_reported_as_not_applied()
    {
        var h = new StubHandler((_, _) => Xml("<boolean>0</boolean>"));
        await Assert.ThrowsAsync<PaperCutNotAppliedException>(() => Client(h).AdjustBalanceAsync("alice", 5m, "r", default));
    }

    [Fact]
    public async Task Fault_response_is_reported_as_not_applied()
    {
        var h = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<methodResponse><fault><value><struct><member><name>faultString</name><value><string>Bad token</string></value></member></struct></value></fault></methodResponse>"),
        });
        var ex = await Assert.ThrowsAsync<PaperCutNotAppliedException>(() => Client(h).AdjustBalanceAsync("alice", 5m, "r", default));
        Assert.Contains("Bad token", ex.Message);
    }

    [Fact]
    public async Task Http_error_and_connection_failure_are_not_applied_but_timeout_is_uncertain()
    {
        var http500 = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<PaperCutNotAppliedException>(() => Client(http500).AdjustBalanceAsync("a", 5m, "r", default));

        var refused = new StubHandler((_, _) => throw new HttpRequestException("refused"));
        await Assert.ThrowsAsync<PaperCutNotAppliedException>(() => Client(refused).AdjustBalanceAsync("a", 5m, "r", default));

        var timeout = new StubHandler((_, _) => throw new TaskCanceledException("timeout"));
        var ex = await Assert.ThrowsAsync<PaperCutException>(() => Client(timeout).AdjustBalanceAsync("a", 5m, "r", default));
        Assert.IsNotType<PaperCutNotAppliedException>(ex);
    }

    [Fact]
    public async Task User_exists_and_balance_are_parsed()
    {
        var exists = new StubHandler((_, _) => Xml("<boolean>1</boolean>"));
        Assert.True(await Client(exists).UserExistsAsync("alice", default));

        var missing = new StubHandler((_, _) => Xml("<boolean>0</boolean>"));
        Assert.False(await Client(missing).UserExistsAsync("ghost", default));

        var balance = new StubHandler((_, _) => Xml("<double>7.5</double>"));
        Assert.Equal(7.5m, await Client(balance).GetBalanceAsync("alice", default));
    }
}
