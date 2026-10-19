using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using PapercutPay;
using PapercutPay.Payments;
using PapercutPay.PaperCut;
using PapercutPay.Web;
using PapercutPay.Worldline;

var builder = WebApplication.CreateBuilder(args);
// Local secrets file (pre-production tests). Never commit it; use environment variables or a protected file in production.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
// Server secrets live outside the application folder so redeploying never touches them.
builder.Configuration.AddJsonFile(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapercutPay", "appsettings.Production.json"),
    optional: true, reloadOnChange: false);
// Keep the usual precedence: local file < environment variables < command line.
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.Configure<PaperCutOptions>(builder.Configuration.GetSection("PaperCut"));
builder.Services.Configure<WorldlineOptions>(builder.Configuration.GetSection("Worldline"));

var app0 = builder.Configuration.GetSection("App").Get<AppOptions>() ?? new AppOptions();
// Fake: in-memory PaperCut and Worldline. Hybrid: real Worldline, in-memory PaperCut (pre-production tests).
var pcProbe = args.Length > 0 && args[0].StartsWith("pc-", StringComparison.Ordinal);
var fakePaperCut = !pcProbe && (app0.Mode.Equals("Fake", StringComparison.OrdinalIgnoreCase) || app0.Mode.Equals("Hybrid", StringComparison.OrdinalIgnoreCase));
var fakeWorldline = app0.Mode.Equals("Fake", StringComparison.OrdinalIgnoreCase);
var fake = fakePaperCut;

builder.Services.AddSingleton<IPaymentStore>(_ => new SqlitePaymentStore(app0.DbPath));
builder.Services.AddSingleton<PaymentService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, app0.RateLimitPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});
builder.Services.AddHostedService<PaymentReconciler>();
if (fakePaperCut)
{
    var pc = new InMemoryPaperCutClient();
    pc.SeedUser("demo", 2.50m);
    builder.Services.AddSingleton<IPaperCutClient>(pc);
}
else
{
    builder.Services.AddHttpClient<IPaperCutClient, XmlRpcPaperCutClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
}

if (fakeWorldline)
{
    builder.Services.AddSingleton<FakeWorldlineClient>();
    builder.Services.AddSingleton<IWorldlineClient>(sp => sp.GetRequiredService<FakeWorldlineClient>());
}
else
{
    builder.Services.AddHttpClient<IWorldlineClient, WorldlineClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
}

var app = builder.Build();

if (args.Length > 0 && args[0] == "orders")
{
    // Operator view of the ledger: dotnet PapercutPay.dll orders [count]. Prints no secrets.
    var count = args.Length > 1 && int.TryParse(args[1], out var n) && n > 0 ? n : 20;
    foreach (var o in new SqlitePaymentStore(app0.DbPath).ListRecent(count))
    {
        Console.WriteLine($"{o.CreatedUtc:yyyy-MM-dd HH:mm:ss}Z  {o.OrderId}  {o.UserName}  {o.AmountMinor / 100m:F2} {o.Currency}  {o.State}  {o.Note}");
    }

    return 0;
}

if (args.Length > 1 && args[0] == "make-link")
{
    // Prints a signed, expiring recharge link path for a PaperCut user: dotnet PapercutPay.dll make-link <user> [hours]
    if (string.IsNullOrWhiteSpace(app0.LinkSecret))
    {
        Console.Error.WriteLine("App:LinkSecret is empty.");
        return 2;
    }

    var hours = args.Length > 2 && double.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 24;
    var token = UserLink.Create(args[1], DateTimeOffset.UtcNow.AddHours(hours), app0.LinkSecret);
    Console.WriteLine($"{app0.PublicBaseUrl.TrimEnd('/')}/pay?t={Uri.EscapeDataString(token)}   (valid {hours:0.##} h)");
    return 0;
}

if (args.Length > 0 && args[0].StartsWith("pc-", StringComparison.Ordinal))
{
    return await Probe.PaperCutAsync(app, args);
}

if (!fakeWorldline || !fakePaperCut)
{
    var missing = new List<string>();
    var pcOpt = app.Services.GetRequiredService<IOptions<PaperCutOptions>>().Value;
    var wlOpt = app.Services.GetRequiredService<IOptions<WorldlineOptions>>().Value;
    if (!fakePaperCut && string.IsNullOrWhiteSpace(pcOpt.BaseUrl)) missing.Add("PaperCut:BaseUrl");
    if (!fakePaperCut && string.IsNullOrWhiteSpace(pcOpt.AuthToken)) missing.Add("PaperCut:AuthToken");
    var worldlineValues = new List<(string, string)> { ("MerchantId", wlOpt.MerchantId), ("ApiKeyId", wlOpt.ApiKeyId), ("ApiSecret", wlOpt.ApiSecret) };
    foreach (var (k, v) in worldlineValues)
    {
        if (string.IsNullOrWhiteSpace(v)) missing.Add("Worldline:" + k);
    }

    if (app0.Amounts.Length == 0) missing.Add("App:Amounts");
    if (string.IsNullOrWhiteSpace(app0.PublicBaseUrl)) missing.Add("App:PublicBaseUrl");
    if ((fakePaperCut || !app0.AllowUnsignedLinks) && string.IsNullOrWhiteSpace(app0.LinkSecret)) missing.Add("App:LinkSecret");
    if (missing.Count > 0)
    {
        throw new InvalidOperationException("Missing configuration: " + string.Join(", ", missing)
            + ". Set them with environment variables (for example Worldline__ApiSecret) or a protected appsettings.Production.json.");
    }
}

if (args.Length > 0 && args[0] == "probe")
{
    return await Probe.RunAsync(app);
}

if (args.Length > 1 && args[0] == "probe-status")
{
    return await Probe.StatusAsync(app, args[1]);
}

if ((fakePaperCut || fakeWorldline) && !app.Environment.IsDevelopment() && !app0.AllowFakeMode)
{
    // Fake and Hybrid expose /dev endpoints that mint payment links and use an in-memory PaperCut.
    throw new InvalidOperationException("App:Mode " + app0.Mode + " is only for test machines. Set App:Mode to Live, or App:AllowFakeMode=true on a test machine.");
}

var appOpt = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
Pages.Configure(appOpt);
if (appOpt.AllowUnsignedLinks && !fakePaperCut)
{
    app.Logger.LogWarning(
        "Unsigned user links are enabled (App:AllowUnsignedLinks): anyone who knows a user name can open the payment form for that account. "
        + "Set it to false to require signed links.");
}

string? ResolveUser(string? signed, string? legacy)
{
    var user = UserLink.TryReadUser(signed, appOpt.LinkSecret, DateTimeOffset.UtcNow);
    if (user is null && appOpt.AllowUnsignedLinks)
    {
        user = UserLink.TryReadPlainUser(legacy);
    }

    return user;
}

static bool LimitBody(HttpRequest req, long bytes)
{
    // Unauthenticated endpoints must not buffer arbitrarily large bodies.
    if (req.ContentLength > bytes)
    {
        return false;
    }

    var feature = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (feature is { IsReadOnly: false })
    {
        feature.MaxRequestBodySize = bytes;
    }

    return true;
}

if (!string.IsNullOrWhiteSpace(appOpt.PathBase))
{
    app.UsePathBase(appOpt.PathBase);
    app.UseRouting();
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Cache-Control"] = "no-store";
    h["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});

if (appOpt.Mode.Equals("Live", StringComparison.OrdinalIgnoreCase) && appOpt.RequireHttps)
{
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.IsHttps && !ctx.Request.Path.StartsWithSegments("/health"))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("HTTPS required.");
            return;
        }

        await next();
    });
}

app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok", mode = appOpt.Mode }));

app.MapGet("/pay", async (HttpRequest req, IPaperCutClient pc, CancellationToken ct) =>
{
    var signed = req.Query["t"].ToString();
    var legacy = req.Query["user"].ToString();
    if (string.IsNullOrEmpty(legacy) && string.IsNullOrEmpty(signed) && UserLink.TryReadLegacyQuery(req.QueryString.Value) is { } fromQuery)
    {
        // Alternative form seen on the old portal: the whole query string is Base64("user=NAME").
        legacy = fromQuery;
    }

    var user = ResolveUser(signed, legacy);
    if (user is null)
    {
        return Results.Content(Pages.Error(Strings.T("err_invalid_link")), "text/html", statusCode: 400);
    }

    try
    {
        if (!await pc.UserExistsAsync(user, ct))
        {
            return Results.Content(Pages.Error(Strings.T("err_account")), "text/html", statusCode: 404);
        }

        // Unsigned legacy links are not authenticated: do not disclose the account balance to whoever typed the name.
        var signedLink = UserLink.TryReadUser(signed, appOpt.LinkSecret, DateTimeOffset.UtcNow) is not null;
        decimal? balance = signedLink ? await pc.GetBalanceAsync(user, ct) : null;
        var (field, value) = signedLink ? ("t", signed) : ("user", legacy);
        return Results.Content(Pages.PaymentForm(user, balance, appOpt.Currency, appOpt.Amounts, field, value, req.PathBase.Value ?? ""), "text/html");
    }
    catch (PaperCutException)
    {
        return Results.Content(Pages.Error(Strings.T("err_papercut")), "text/html", statusCode: 503);
    }
});

app.MapPost("/pay/start", async (HttpRequest req, PaymentService svc, ILogger<Program> log, CancellationToken ct) =>
{
    if (!LimitBody(req, 16 * 1024))
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var form = await req.ReadFormAsync(ct);
    var user = ResolveUser(form["t"].ToString(), form["user"].ToString());
    if (user is null || !decimal.TryParse(form["amount"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount))
    {
        return Results.Content(Pages.Error(Strings.T("err_request")), "text/html", statusCode: 400);
    }

    try
    {
        var started = await svc.StartAsync(user, amount, ct);
        return Results.Redirect(started.RedirectUrl, permanent: false, preserveMethod: false);
    }
    catch (ArgumentException)
    {
        return Results.Content(Pages.Error(Strings.T("err_request")), "text/html", statusCode: 400);
    }
    catch (Exception ex) when (ex is WorldlineException or PaperCutException or HttpRequestException)
    {
        log.LogError(ex, "Could not start payment for {User}", user);
        return Results.Content(Pages.Error(Strings.T("err_payment")), "text/html", statusCode: 503);
    }
});

app.MapGet("/pay/return", async (string? o, PaymentService svc, ILogger<Program> log, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(o))
    {
        return Results.Content(Pages.Result(CompletionOutcome.NotFound, null, null), "text/html", statusCode: 404);
    }

    try
    {
        var outcome = await svc.CompleteAsync(o, ct);
        return Results.Content(Pages.Result(outcome, appOpt.PaperCutUserUrl, o), "text/html");
    }
    catch (Exception ex) when (ex is WorldlineException or HttpRequestException)
    {
        log.LogError(ex, "Could not verify order {Order}", o);
        return Results.Content(Pages.Result(CompletionOutcome.Pending, appOpt.PaperCutUserUrl, o), "text/html");
    }
});

app.MapMethods("/webhooks/worldline", ["GET", "POST"], async (HttpRequest req, PaymentService svc, IOptions<WorldlineOptions> wl, ILogger<Program> log, CancellationToken ct) =>
{
    // Endpoint verification handshake: echo the challenge back (see Worldline webhooks guide; confirm in pre-production).
    if (req.Headers.TryGetValue("X-GCS-Webhooks-Endpoint-Verification", out var challenge))
    {
        return Results.Text(challenge.ToString());
    }

    if (!LimitBody(req, 256 * 1024))
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms, ct);
    var raw = ms.ToArray();

    if (!WebhookVerifier.IsValid(raw, req.Headers["X-GCS-Signature"], req.Headers["X-GCS-KeyId"], wl.Value.WebhookKeyId, wl.Value.WebhookSecret))
    {
        log.LogWarning("Rejected webhook with invalid signature from {Ip}", req.HttpContext.Connection.RemoteIpAddress);
        return Results.Unauthorized();
    }

    JsonNode? payload;
    try
    {
        payload = JsonNode.Parse(raw);
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.BadRequest();
    }

    // The payload is only a hint to find our order. The credit decision comes from GetHostedCheckout.
    var orderId = WebhookVerifier.FindMerchantReference(payload);
    if (orderId is not null)
    {
        try
        {
            var outcome = await svc.CompleteAsync(orderId, ct);
            log.LogInformation("Webhook for order {Order}: {Outcome}", orderId, outcome);
        }
        catch (Exception ex) when (ex is WorldlineException or HttpRequestException)
        {
            log.LogError(ex, "Webhook for order {Order} could not be verified", orderId);
            return Results.StatusCode(503); // ask Worldline to retry
        }
    }

    return Results.Ok();
});

if (fakeWorldline)
{
    app.MapGet("/dev/fake-checkout/{id}", (string id, HttpRequest req) => Results.Content(Pages.FakeCheckout(id, id, req.PathBase.Value ?? ""), "text/html"));
    app.MapPost("/dev/fake-checkout/{id}", async (string id, HttpRequest req, FakeWorldlineClient fw) =>
    {
        var form = await req.ReadFormAsync();
        if (form["action"] == "pay") fw.Pay(id); else fw.Cancel(id);
        return Results.Redirect(fw.ReturnUrlOf(id));
    });
}

if (fake)
{
    app.MapGet("/dev/link/{user}", (string user, HttpRequest req) =>
        Results.Redirect(req.PathBase + "/pay?t=" + Uri.EscapeDataString(UserLink.Create(user, DateTimeOffset.UtcNow.AddHours(1), appOpt.LinkSecret))));
}

app.Run();
return 0;

public partial class Program;
