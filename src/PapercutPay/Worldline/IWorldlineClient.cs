namespace PapercutPay.Worldline;

public enum CheckoutVerdict
{
    /// <summary>Money is secured; the PaperCut account may be credited.</summary>
    Paid,

    /// <summary>Nothing final yet (customer still on the page, authentication pending, ...).</summary>
    Pending,

    /// <summary>Final failure: cancelled, refused or expired.</summary>
    Failed,
}

public sealed record HostedCheckoutCreated(string HostedCheckoutId, string RedirectUrl);

public sealed record HostedCheckoutStatus(
    CheckoutVerdict Verdict,
    string RawStatus,
    string? PaymentId,
    long? AmountMinor,
    string? Currency,
    string? MerchantReference);

public interface IWorldlineClient
{
    Task<HostedCheckoutCreated> CreateHostedCheckoutAsync(
        string merchantReference, long amountMinor, string currency, string returnUrl, string locale, CancellationToken ct);

    Task<HostedCheckoutStatus> GetHostedCheckoutAsync(string hostedCheckoutId, CancellationToken ct);
}
