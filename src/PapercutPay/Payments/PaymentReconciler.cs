using Microsoft.Extensions.Options;

namespace PapercutPay.Payments;

/// <summary>
/// Safety net for browsers that never come back from Worldline: periodically re-checks open orders with the same
/// server-to-server verification and the same atomic claim as the browser return, so it can never credit twice.
/// </summary>
public sealed class PaymentReconciler(
    PaymentService payments,
    IPaymentStore store,
    IOptions<AppOptions> options,
    TimeProvider clock,
    ILogger<PaymentReconciler> log) : BackgroundService
{
    private const int BatchSize = 50;
    private readonly AppOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _opt.ReconcileIntervalSeconds)), clock);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Reconciliation pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One pass. Returns how many orders ended up credited by this pass.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = store.ListOpenOrderIds(
            now.AddSeconds(-_opt.ReconcileMinAgeSeconds), now.AddHours(-_opt.ReconcileMaxAgeHours), BatchSize);

        foreach (var stuck in store.ListStuckCrediting(now.AddMinutes(-_opt.StuckCreditMinutes), BatchSize))
        {
            store.SetState(stuck, PaymentState.CreditUncertain,
                "Interrupted while crediting. Compare the PaperCut balance with the payment before any manual action.");
            log.LogError("Order {Order} was stuck in CREDITING and is now flagged CREDIT_UNCERTAIN", stuck);
        }

        var credited = 0;
        foreach (var id in ids)
        {
            try
            {
                var outcome = await payments.CompleteAsync(id, ct);
                if (outcome == CompletionOutcome.Credited)
                {
                    credited++;
                    log.LogInformation("Reconciliation credited order {Order}", id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failing order (Worldline hiccup, ...) must not block the others; it is retried next pass.
                log.LogWarning(ex, "Reconciliation could not check order {Order}", id);
            }
        }

        return credited;
    }
}
