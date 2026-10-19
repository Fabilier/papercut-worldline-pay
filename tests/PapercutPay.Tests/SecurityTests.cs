using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PapercutPay.PaperCut;
using PapercutPay.Payments;
using PapercutPay.Worldline;

namespace PapercutPay.Tests;

[Collection("EnvironmentBound")]
public sealed class HttpSecurityTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"pcsec-{Guid.NewGuid():N}.db");
    private readonly List<string> _vars = [];
    private WebApplicationFactory<Program>? _factory;

    private void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _vars.Add(name);
    }

    private HttpClient Fake(string? environment = null)
    {
        Set("App__Mode", "Fake");
        Set("App__LinkSecret", "test-secret");
        Set("App__DbPath", _db);
        return Create("http://localhost", environment);
    }

    private HttpClient Create(string baseAddress, string? environment)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (environment is not null)
            {
                b.UseEnvironment(environment);
            }
        });
        return _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri(baseAddress) });
    }

    private HttpClient Live(string baseAddress)
    {
        Set("App__Mode", "Live");
        Set("App__PublicBaseUrl", "https://pay.example.org");
        Set("App__LinkSecret", "test-secret");
        Set("App__DbPath", _db);
        Set("PaperCut__BaseUrl", "http://papercut.invalid:9191");
        Set("PaperCut__AuthToken", "tok");
        Set("Worldline__MerchantId", "m");
        Set("Worldline__ApiKeyId", "k");
        Set("Worldline__ApiSecret", "s");
        return Create(baseAddress, null);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var v in _vars)
        {
            Environment.SetEnvironmentVariable(v, null);
        }

        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
        {
            File.Delete(f);
        }
    }

    [Fact]
    public async Task Responses_carry_hardening_headers_and_are_not_cacheable()
    {
        var r = await Fake().GetAsync("/health");

        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", r.Headers.GetValues("Cache-Control").Single());
        Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public void Fake_mode_is_refused_outside_development()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Fake(environment: "Production"));
        Assert.Contains("only for test machines", ex.ToString());
    }

    [Fact]
    public async Task Fake_mode_is_allowed_outside_development_only_with_the_explicit_flag()
    {
        Set("App__AllowFakeMode", "true");
        var r = await Fake(environment: "Production").GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Live_mode_refuses_plain_http_but_keeps_health_open()
    {
        var http = Live("http://localhost");
        var pay = await http.GetAsync("/pay?t=x");
        Assert.Equal(HttpStatusCode.BadRequest, pay.StatusCode);
        Assert.Contains("HTTPS required", await pay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);

        var https = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var over = await https.GetAsync("/pay?t=garbage");
        Assert.DoesNotContain("HTTPS required", await over.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Too_many_requests_from_one_address_are_throttled()
    {
        Set("App__RateLimitPerMinute", "3");
        var client = Fake();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            codes.Add((await client.GetAsync("/health")).StatusCode);
        }

        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Unsigned_legacy_link_does_not_disclose_the_balance_but_signed_link_does()
    {
        var client = Fake();

        var legacyHtml = await (await client.GetAsync("/pay?user=demo")).Content.ReadAsStringAsync();
        Assert.Contains("demo", legacyHtml);
        Assert.DoesNotContain("Current balance", legacyHtml);

        var link = await client.GetAsync("/dev/link/demo");
        var signedHtml = await (await client.GetAsync(link.Headers.Location)).Content.ReadAsStringAsync();
        Assert.Contains("Current balance", signedHtml);
    }

    [Fact]
    public async Task Link_in_the_format_sent_by_papercut_opens_the_form_without_balance()
    {
        var client = Fake();
        var query = Convert.ToBase64String("user=demo"u8.ToArray());

        var html = await (await client.GetAsync("/pay?" + query)).Content.ReadAsStringAsync();

        Assert.Contains("demo", html);
        Assert.DoesNotContain("Current balance", html);
        Assert.Contains("name=\"user\" value=\"demo\"", html);
    }

    [Fact]
    public async Task Plain_user_link_as_built_by_papercut_opens_the_form_and_starts_a_payment()
    {
        var client = Fake();

        var form = await (await client.GetAsync("/pay?user=demo")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"user\" value=\"demo\"", form);

        var start = await client.PostAsync("/pay/start", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("user", "demo"), new KeyValuePair<string, string>("amount", "10")]));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Contains("/dev/fake-checkout/", start.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Oversized_bodies_are_rejected_before_any_processing()
    {
        var client = Fake();
        var big = new StringContent(new string('a', 300 * 1024), Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/webhooks/worldline", big)).StatusCode);
    }

    [Fact]
    public async Task Webhook_without_valid_signature_is_unauthorized()
    {
        var client = Fake();
        var r = await client.PostAsync("/webhooks/worldline", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

public class XmlHardeningTests
{
    private sealed class Stub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Fact]
    public async Task Answer_with_a_dtd_is_refused_and_not_treated_as_a_credit()
    {
        const string evil = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]><methodResponse><params><param><value><boolean>1</boolean></value></param></params></methodResponse>";
        var client = new XmlRpcPaperCutClient(new HttpClient(new Stub(evil)), Options.Create(new PaperCutOptions { BaseUrl = "http://pc", AuthToken = "t" }));

        var ex = await Assert.ThrowsAsync<PaperCutException>(() => client.AdjustBalanceAsync("a", 5m, "r", default));
        Assert.IsNotType<PaperCutNotAppliedException>(ex);
    }
}

public sealed class LedgerHardeningTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"pcled-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
        {
            File.Delete(f);
        }
    }

    [Fact]
    public void Database_created_by_an_earlier_version_is_upgraded_in_place()
    {
        using (var c = new SqliteConnection($"Data Source={_db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE payments (order_id TEXT PRIMARY KEY, user_name TEXT NOT NULL, amount_minor INTEGER NOT NULL,
                    currency TEXT NOT NULL, state TEXT NOT NULL, hosted_checkout_id TEXT UNIQUE, payment_id TEXT, note TEXT, created_utc TEXT NOT NULL);
                INSERT INTO payments VALUES ('PCOLD','alice',500,'EUR','CREATED','co1',NULL,NULL,'2026-10-19T10:00:00.0000000Z');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new SqlitePaymentStore(_db);

        Assert.Equal("PCOLD", store.Get("PCOLD")!.OrderId);
        Assert.True(store.TryClaimForCredit("PCOLD", "pay1"));
        Assert.Equal(PaymentState.Crediting, store.Get("PCOLD")!.State);
    }

    private (PaymentReconciler Reconciler, SqlitePaymentStore Store) Build(int stuckMinutes)
    {
        var store = new SqlitePaymentStore(_db);
        var opt = Options.Create(new AppOptions { Amounts = [5m], StuckCreditMinutes = stuckMinutes });
        var svc = new PaymentService(store, new InMemoryPaperCutClient(), new FakeWorldlineClient(), opt, NullLogger<PaymentService>.Instance);
        return (new PaymentReconciler(svc, store, opt, TimeProvider.System, NullLogger<PaymentReconciler>.Instance), store);
    }

    private static void Claim(SqlitePaymentStore store)
    {
        store.Insert(new PaymentRecord("PC1", "alice", 500, "EUR", PaymentState.Created, "co1", null, null, DateTime.UtcNow.AddMinutes(-30)));
        Assert.True(store.TryClaimForCredit("PC1", "pay1"));
    }

    [Fact]
    public async Task Order_stuck_in_crediting_is_flagged_for_review()
    {
        var (reconciler, store) = Build(stuckMinutes: 0);
        Claim(store);
        await Task.Delay(20);

        await reconciler.RunOnceAsync(default);

        var order = store.Get("PC1")!;
        Assert.Equal(PaymentState.CreditUncertain, order.State);
        Assert.Contains("PaperCut balance", order.Note);
    }

    [Fact]
    public async Task Order_being_credited_right_now_is_left_alone()
    {
        var (reconciler, store) = Build(stuckMinutes: 10);
        Claim(store);

        await reconciler.RunOnceAsync(default);

        Assert.Equal(PaymentState.Crediting, store.Get("PC1")!.State);
    }
}
