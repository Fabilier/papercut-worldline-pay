using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PapercutPay.Worldline;

/// <summary>
/// Worldline Direct Hosted Checkout client (CreateHostedCheckout / GetHostedCheckout) over REST with v1HMAC auth.
/// Paths and JSON field names follow the Direct API; confirm them against the API Explorer
/// (https://explorer.direct.worldline-solutions.com/) during the pre-production test.
/// </summary>
public sealed class WorldlineClient(HttpClient http, IOptions<WorldlineOptions> options) : IWorldlineClient
{
    private readonly WorldlineOptions _opt = options.Value;

    /// <summary>Optional trace of each exchange (status line and body), used by the probe command. Never contains secrets.</summary>
    public Action<string>? Trace { get; set; }

    public async Task<HostedCheckoutCreated> CreateHostedCheckoutAsync(
        string merchantReference, long amountMinor, string currency, string returnUrl, string locale, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["order"] = new JsonObject
            {
                ["amountOfMoney"] = new JsonObject { ["amount"] = amountMinor, ["currencyCode"] = currency },
                ["references"] = new JsonObject { ["merchantReference"] = merchantReference },
            },
            ["hostedCheckoutSpecificInput"] = new JsonObject { ["returnUrl"] = returnUrl, ["locale"] = locale },
            // The account default is authorization only (PENDING_CAPTURE). SALE captures at once, so the payment ends as CAPTURED.
            ["cardPaymentMethodSpecificInput"] = new JsonObject { ["authorizationMode"] = "SALE" },
        };

        var json = await SendAsync(HttpMethod.Post, $"/{_opt.ApiVersion}/{_opt.MerchantId}/hostedcheckouts", body.ToJsonString(), ct);
        var id = json["hostedCheckoutId"]?.GetValue<string>() ?? throw new WorldlineException("Missing hostedCheckoutId.");
        var redirect = json["redirectUrl"]?.GetValue<string>() ?? throw new WorldlineException("Missing redirectUrl.");
        return new HostedCheckoutCreated(id, redirect);
    }

    public async Task<HostedCheckoutStatus> GetHostedCheckoutAsync(string hostedCheckoutId, CancellationToken ct)
    {
        var json = await SendAsync(
            HttpMethod.Get, $"/{_opt.ApiVersion}/{_opt.MerchantId}/hostedcheckouts/{Uri.EscapeDataString(hostedCheckoutId)}", null, ct);
        return ParseStatus(json, _opt);
    }

    internal static HostedCheckoutStatus ParseStatus(JsonNode json, WorldlineOptions opt)
    {
        var hostedStatus = json["status"]?.GetValue<string>() ?? "UNKNOWN";
        var payment = json["createdPaymentOutput"]?["payment"];
        var paymentStatus = payment?["status"]?.GetValue<string>();
        var amountOfMoney = payment?["paymentOutput"]?["amountOfMoney"];

        long? amount = amountOfMoney?["amount"] is { } a ? a.GetValue<long>() : null;
        var verdict = Classify(hostedStatus, paymentStatus, opt);

        return new HostedCheckoutStatus(
            verdict,
            paymentStatus ?? hostedStatus,
            payment?["id"]?.GetValue<string>(),
            amount,
            amountOfMoney?["currencyCode"]?.GetValue<string>(),
            payment?["paymentOutput"]?["references"]?["merchantReference"]?.GetValue<string>());
    }

    internal static CheckoutVerdict Classify(string hostedStatus, string? paymentStatus, WorldlineOptions opt)
    {
        if (paymentStatus is not null)
        {
            if (opt.EffectivePaidStatuses.Contains(paymentStatus, StringComparer.OrdinalIgnoreCase))
            {
                return CheckoutVerdict.Paid;
            }

            if (opt.EffectiveFailedStatuses.Contains(paymentStatus, StringComparer.OrdinalIgnoreCase))
            {
                return CheckoutVerdict.Failed;
            }

            return CheckoutVerdict.Pending;
        }

        return hostedStatus switch
        {
            "CANCELLED_BY_CONSUMER" or "CLIENT_NOT_ELIGIBLE_FOR_SELECTED_PAYMENT_PRODUCT" => CheckoutVerdict.Failed,
            _ => CheckoutVerdict.Pending,
        };
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var date = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
        var contentType = body is null ? "" : "application/json";
        var toSign = GcsSigner.BuildStringToSign(method.Method, contentType, date, new Dictionary<string, string>(), path);
        var signature = GcsSigner.Sign(toSign, _opt.ApiSecret);

        using var request = new HttpRequestMessage(method, _opt.BaseUrl.TrimEnd('/') + path);
        request.Headers.TryAddWithoutValidation("Date", date);
        request.Headers.TryAddWithoutValidation("Authorization", GcsSigner.AuthorizationHeader(_opt.ApiKeyId, signature));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Trace?.Invoke($"{method} {path} -> HTTP {(int)response.StatusCode}{Environment.NewLine}{text}");
        if (!response.IsSuccessStatusCode)
        {
            throw new WorldlineException($"Worldline returned HTTP {(int)response.StatusCode}: {text}");
        }

        return JsonNode.Parse(text) ?? throw new WorldlineException("Empty response.");
    }
}

public sealed class WorldlineException(string message) : Exception(message);
