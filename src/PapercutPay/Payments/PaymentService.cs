using Microsoft.Extensions.Options;
using PapercutPay.PaperCut;
using PapercutPay.Worldline;

namespace PapercutPay.Payments;

public enum CompletionOutcome
{
    Credited,
    AlreadyCredited,
    Pending,
    Failed,

    /// <summary>Paid but not credited automatically (mismatch or unknown PaperCut outcome). Operator action needed.</summary>
    NeedsReview,

    /// <summary>Paid, PaperCut was unreachable, nothing was applied. Calling again retries safely.</summary>
    RetryLater,
    NotFound,
}

public sealed record StartResult(string OrderId, string RedirectUrl);

public sealed class PaymentService(
    IPaymentStore store,
    IPaperCutClient paperCut,
    IWorldlineClient worldline,
    IOptions<AppOptions> options,
    ILogger<PaymentService> log)
{
    private readonly AppOptions _opt = options.Value;

    public async Task<StartResult> StartAsync(string userName, decimal amount, CancellationToken ct)
    {
        if (!_opt.Amounts.Contains(amount))
        {
            throw new ArgumentException("Amount is not one of the offered amounts.", nameof(amount));
        }

        if (!await paperCut.UserExistsAsync(userName, ct))
        {
            throw new ArgumentException("Unknown PaperCut user.", nameof(userName));
        }

        var orderId = "PC" + Guid.NewGuid().ToString("N")[..18].ToUpperInvariant();
        var amountMinor = (long)decimal.Round(amount * 100m);
        store.Insert(new PaymentRecord(orderId, userName, amountMinor, _opt.Currency, PaymentState.Created, null, null, null, DateTime.UtcNow));

        var returnUrl = $"{_opt.PublicBaseUrl.TrimEnd('/')}/pay/return?o={orderId}";
        var created = await worldline.CreateHostedCheckoutAsync(orderId, amountMinor, _opt.Currency, returnUrl, _opt.Locale, ct);
        store.SetHostedCheckoutId(orderId, created.HostedCheckoutId);
        log.LogInformation("Order {Order} created for {User}, {Amount} {Currency}", orderId, userName, amount, _opt.Currency);
        return new StartResult(orderId, created.RedirectUrl);
    }

    /// <summary>
    /// Single entry point for the browser return and the webhook. The decision to credit is always taken from the
    /// server-to-server Worldline response and the stored order, never from request parameters.
    /// </summary>
    public async Task<CompletionOutcome> CompleteAsync(string orderId, CancellationToken ct)
    {
        var order = store.Get(orderId);
        if (order is null)
        {
            return CompletionOutcome.NotFound;
        }

        switch (order.State)
        {
            case PaymentState.Credited:
                return CompletionOutcome.AlreadyCredited;
            case PaymentState.Failed:
                return CompletionOutcome.Failed;
            case PaymentState.Review or PaymentState.CreditUncertain:
                return CompletionOutcome.NeedsReview;
            case PaymentState.Crediting:
                return CompletionOutcome.Pending;
        }

        if (order.HostedCheckoutId is null)
        {
            return CompletionOutcome.Pending;
        }

        var status = await worldline.GetHostedCheckoutAsync(order.HostedCheckoutId, ct);
        switch (status.Verdict)
        {
            case CheckoutVerdict.Pending:
                return CompletionOutcome.Pending;
            case CheckoutVerdict.Failed:
                store.SetState(orderId, PaymentState.Failed, status.RawStatus);
                return CompletionOutcome.Failed;
        }

        if (status.PaymentId is null
            || status.AmountMinor != order.AmountMinor
            || !string.Equals(status.Currency, order.Currency, StringComparison.OrdinalIgnoreCase)
            || (status.MerchantReference is not null && status.MerchantReference != order.OrderId))
        {
            var note = $"Mismatch: paid {status.AmountMinor} {status.Currency} ref {status.MerchantReference}, expected {order.AmountMinor} {order.Currency} ref {order.OrderId}";
            store.SetState(orderId, PaymentState.Review, note);
            log.LogError("Order {Order} needs review. {Note}", orderId, note);
            return CompletionOutcome.NeedsReview;
        }

        if (!store.TryClaimForCredit(orderId, status.PaymentId))
        {
            // Another request (browser return or webhook) is crediting or has credited.
            return store.Get(orderId)?.State == PaymentState.Credited ? CompletionOutcome.AlreadyCredited : CompletionOutcome.Pending;
        }

        try
        {
            await paperCut.AdjustBalanceAsync(
                order.UserName, order.AmountMinor / 100m, $"Worldline {order.OrderId} payment {status.PaymentId}", ct);
        }
        catch (PaperCutNotAppliedException ex)
        {
            store.SetState(orderId, PaymentState.Created, "PaperCut refused or unreachable: " + ex.Message);
            log.LogWarning(ex, "Order {Order} paid but PaperCut did not apply the credit; will retry", orderId);
            return CompletionOutcome.RetryLater;
        }
        catch (Exception ex)
        {
            store.SetState(orderId, PaymentState.CreditUncertain, ex.Message);
            log.LogError(ex, "Order {Order} paid; PaperCut outcome unknown. Check the balance before retrying", orderId);
            return CompletionOutcome.NeedsReview;
        }

        store.SetState(orderId, PaymentState.Credited);
        log.LogInformation("Order {Order} credited to {User}", orderId, order.UserName);
        return CompletionOutcome.Credited;
    }
}
