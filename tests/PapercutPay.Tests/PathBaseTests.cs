using Microsoft.AspNetCore.Mvc.Testing;

namespace PapercutPay.Tests;

/// <summary>The service runs as an IIS application under a virtual path; links and form targets must keep it.</summary>
[Collection("EnvironmentBound")]
public sealed class PathBaseTests : IDisposable
{
    private static readonly string[] Variables = ["App__Mode", "App__LinkSecret", "App__DbPath", "App__PathBase"];
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"pcpb-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program>? _factory;

    private HttpClient Client(string? pathBase)
    {
        // Environment variables outrank the local settings files, so a developer's secrets never leak into tests.
        Environment.SetEnvironmentVariable("App__Mode", "Fake");
        Environment.SetEnvironmentVariable("App__LinkSecret", "test-secret");
        Environment.SetEnvironmentVariable("App__DbPath", _db);
        Environment.SetEnvironmentVariable("App__PathBase", pathBase);
        _factory = new WebApplicationFactory<Program>();
        return _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var name in Variables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
        {
            File.Delete(f);
        }
    }

    [Fact]
    public async Task Form_and_links_keep_the_virtual_path()
    {
        var client = Client("/PapercutPay");

        var link = await client.GetAsync("/PapercutPay/dev/link/demo");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, link.StatusCode);
        Assert.StartsWith("/PapercutPay/pay?t=", link.Headers.Location!.OriginalString);

        var page = await client.GetAsync(link.Headers.Location);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("action=\"/PapercutPay/pay/start\"", html);
    }

    [Fact]
    public async Task Without_a_virtual_path_the_form_posts_to_the_site_root()
    {
        var client = Client(null);

        var link = await client.GetAsync("/dev/link/demo");
        var html = await (await client.GetAsync(link.Headers.Location)).Content.ReadAsStringAsync();

        Assert.Contains("action=\"/pay/start\"", html);
    }

    [Fact]
    public async Task Health_endpoint_answers_under_the_virtual_path()
    {
        var client = Client("/PapercutPay");
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/PapercutPay/health")).StatusCode);
    }
}
