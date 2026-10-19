using Microsoft.Extensions.Options;
using System.Globalization;
using PapercutPay.PaperCut;
using PapercutPay.Worldline;

namespace PapercutPay;

/// <summary>
/// "dotnet PapercutPay.dll probe": creates a 1.00 EUR Hosted Checkout in the configured Worldline environment and
/// prints the raw exchanges, so paths and field names can be compared with the API Explorer. No money moves
/// until someone pays on the printed URL. Secrets are never printed.
/// </summary>
public static class Probe
{
    public static async Task<int> RunAsync(WebApplication app)
    {
        if (app.Services.GetRequiredService<IWorldlineClient>() is not WorldlineClient client)
        {
            Console.Error.WriteLine("Probe needs Mode=Live or Mode=Hybrid (real Worldline client).");
            return 2;
        }

        var wl = app.Services.GetRequiredService<IOptions<WorldlineOptions>>().Value;
        var appOpt = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
        Console.WriteLine($"Target: {wl.BaseUrl}  merchant: {wl.MerchantId}  api key id: {wl.ApiKeyId}");
        client.Trace = Console.WriteLine;

        var reference = "PROBE" + DateTime.UtcNow.ToString("yyMMddHHmmss");
        var returnUrl = $"{appOpt.PublicBaseUrl.TrimEnd('/')}/pay/return?o={reference}";
        try
        {
            var created = await client.CreateHostedCheckoutAsync(reference, 100, appOpt.Currency, returnUrl, appOpt.Locale, default);
            Console.WriteLine($"\nCreated hostedCheckoutId={created.HostedCheckoutId}\nOpen to pay: {created.RedirectUrl}");

            var status = await client.GetHostedCheckoutAsync(created.HostedCheckoutId, default);
            Console.WriteLine($"\nParsed before payment: verdict={status.Verdict} raw={status.RawStatus}");
            Console.WriteLine($"\nAfter paying with a Worldline test card, run:  probe-status {created.HostedCheckoutId}");
            return 0;
        }
        catch (WorldlineException ex)
        {
            Console.Error.WriteLine("\nWorldline call failed: " + ex.Message);
            return 1;
        }
    }

    public static async Task<int> StatusAsync(WebApplication app, string hostedCheckoutId)
    {
        if (app.Services.GetRequiredService<IWorldlineClient>() is not WorldlineClient client)
        {
            return 2;
        }

        client.Trace = Console.WriteLine;
        try
        {
            var s = await client.GetHostedCheckoutAsync(hostedCheckoutId, default);
            Console.WriteLine($"\nParsed: verdict={s.Verdict} raw={s.RawStatus} paymentId={s.PaymentId} amountMinor={s.AmountMinor} currency={s.Currency} ref={s.MerchantReference}");
            return 0;
        }
        catch (WorldlineException ex)
        {
            Console.Error.WriteLine("\nWorldline call failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// "pc-check &lt;user&gt;": read-only (user exists, balance).
    /// "pc-adjust &lt;user&gt; &lt;amount&gt;": WRITES to PaperCut (positive or negative adjustment); use a test account.
    /// </summary>
    public static async Task<int> PaperCutAsync(WebApplication app, string[] args)
    {
        if (app.Services.GetRequiredService<IPaperCutClient>() is not XmlRpcPaperCutClient pc)
        {
            Console.Error.WriteLine("PaperCut probes need App__Mode=Live (real PaperCut client).");
            return 2;
        }

        var opt = app.Services.GetRequiredService<IOptions<PaperCutOptions>>().Value;
        if (string.IsNullOrWhiteSpace(opt.BaseUrl) || string.IsNullOrWhiteSpace(opt.AuthToken))
        {
            Console.Error.WriteLine("Set PaperCut:BaseUrl and PaperCut:AuthToken (appsettings.Local.json).");
            return 2;
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: pc-check <user> | pc-adjust <user> <amount>");
            return 2;
        }

        Console.WriteLine($"Target: {opt.BaseUrl}  account: {(string.IsNullOrWhiteSpace(opt.AccountName) ? "(primary)" : opt.AccountName)}");
        try
        {
            var user = args[1];
            Console.WriteLine($"isUserExists({user}) = {await pc.UserExistsAsync(user, default)}");
            Console.WriteLine($"balance before = {await pc.GetBalanceAsync(user, default):F2}");
            if (args[0] == "pc-adjust")
            {
                if (args.Length < 3 || !decimal.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                {
                    Console.Error.WriteLine("pc-adjust needs an amount, for example 0.01 or -0.01.");
                    return 2;
                }

                await pc.AdjustBalanceAsync(user, amount, "PapercutPay probe", default);
                Console.WriteLine($"adjust {amount:F2}: OK");
                Console.WriteLine($"balance after  = {await pc.GetBalanceAsync(user, default):F2}");
            }

            return 0;
        }
        catch (PaperCutException ex)
        {
            Console.Error.WriteLine("PaperCut call failed: " + ex.Message + (ex.InnerException is null ? "" : " / " + ex.InnerException.Message));
            return 1;
        }
    }
}
