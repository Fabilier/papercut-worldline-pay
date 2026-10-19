using System.Collections.Concurrent;

namespace PapercutPay.Worldline;

/// <summary>Stand-in for Fake mode and tests. Outcomes are driven by <see cref="Pay"/> and <see cref="Cancel"/>.</summary>
public sealed class FakeWorldlineClient(string checkoutBaseUrl = "/dev/fake-checkout") : IWorldlineClient
{
    private sealed record Checkout(string Reference, long AmountMinor, string Currency, string ReturnUrl)
    {
        public CheckoutVerdict Verdict { get; set; } = CheckoutVerdict.Pending;
        public string? PaymentId { get; set; }
        public long? ReportedAmount { get; set; }
    }

    private readonly ConcurrentDictionary<string, Checkout> _checkouts = new();

    public Task<HostedCheckoutCreated> CreateHostedCheckoutAsync(
        string merchantReference, long amountMinor, string currency, string returnUrl, string locale, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        _checkouts[id] = new Checkout(merchantReference, amountMinor, currency, returnUrl);
        return Task.FromResult(new HostedCheckoutCreated(id, $"{checkoutBaseUrl}/{id}"));
    }

    public Task<HostedCheckoutStatus> GetHostedCheckoutAsync(string hostedCheckoutId, CancellationToken ct)
    {
        var c = _checkouts[hostedCheckoutId];
        return Task.FromResult(new HostedCheckoutStatus(
            c.Verdict, c.Verdict.ToString().ToUpperInvariant(), c.PaymentId, c.ReportedAmount ?? c.AmountMinor, c.Currency, c.Reference));
    }

    public string ReturnUrlOf(string hostedCheckoutId) => _checkouts[hostedCheckoutId].ReturnUrl;

    /// <summary>Mark the checkout as paid. <paramref name="reportedAmountMinor"/> lets tests simulate a mismatching amount.</summary>
    public void Pay(string hostedCheckoutId, long? reportedAmountMinor = null)
    {
        var c = _checkouts[hostedCheckoutId];
        c.Verdict = CheckoutVerdict.Paid;
        c.PaymentId = "fake-" + hostedCheckoutId[..8];
        c.ReportedAmount = reportedAmountMinor;
    }

    public void Cancel(string hostedCheckoutId) => _checkouts[hostedCheckoutId].Verdict = CheckoutVerdict.Failed;
}
