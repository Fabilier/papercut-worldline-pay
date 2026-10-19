using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PapercutPay.PaperCut;
using PapercutPay.Payments;
using PapercutPay.Worldline;

namespace PapercutPay.Tests;

public sealed class PaymentServiceTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"pcpay-{Guid.NewGuid():N}.db");
    private readonly InMemoryPaperCutClient _pc = new();
    private readonly FakeWorldlineClient _wl = new();
    private readonly PaymentService _svc;
    private readonly SqlitePaymentStore _store;

    public PaymentServiceTests()
    {
        _pc.SeedUser("alice", 1m);
        _store = new SqlitePaymentStore(_db);
        _svc = new PaymentService(_store, _pc, _wl, Options.Create(new AppOptions { Amounts = [5m, 10m] }), NullLogger<PaymentService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
        {
            File.Delete(f);
        }
    }

    private async Task<(string OrderId, string CheckoutId)> StartAsync(decimal amount = 10m)
    {
        var started = await _svc.StartAsync("alice", amount, default);
        return (started.OrderId, _store.Get(started.OrderId)!.HostedCheckoutId!);
    }

    [Fact]
    public async Task Paid_order_credits_exactly_once_even_if_return_and_webhook_race()
    {
        var (order, checkout) = await StartAsync();
        _wl.Pay(checkout);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => _svc.CompleteAsync(order, default))));

        Assert.Single(_pc.Adjustments);
        Assert.Equal(("alice", 10m), (_pc.Adjustments[0].User, _pc.Adjustments[0].Amount));
        Assert.Equal(11m, await _pc.GetBalanceAsync("alice", default));
        Assert.Equal(1, outcomes.Count(o => o == CompletionOutcome.Credited));
        Assert.Equal(PaymentState.Credited, _store.Get(order)!.State);
    }

    [Fact]
    public async Task Repeated_return_after_credit_does_not_credit_again()
    {
        var (order, checkout) = await StartAsync();
        _wl.Pay(checkout);

        Assert.Equal(CompletionOutcome.Credited, await _svc.CompleteAsync(order, default));
        Assert.Equal(CompletionOutcome.AlreadyCredited, await _svc.CompleteAsync(order, default));
        Assert.Single(_pc.Adjustments);
    }

    [Fact]
    public async Task Unpaid_order_is_pending_and_not_credited()
    {
        var (order, _) = await StartAsync();

        Assert.Equal(CompletionOutcome.Pending, await _svc.CompleteAsync(order, default));
        Assert.Empty(_pc.Adjustments);
    }

    [Fact]
    public async Task Cancelled_order_is_failed_and_not_credited()
    {
        var (order, checkout) = await StartAsync();
        _wl.Cancel(checkout);

        Assert.Equal(CompletionOutcome.Failed, await _svc.CompleteAsync(order, default));
        Assert.Equal(CompletionOutcome.Failed, await _svc.CompleteAsync(order, default));
        Assert.Empty(_pc.Adjustments);
    }

    [Fact]
    public async Task Amount_mismatch_is_not_credited_and_flagged_for_review()
    {
        var (order, checkout) = await StartAsync(10m);
        _wl.Pay(checkout, reportedAmountMinor: 500);

        Assert.Equal(CompletionOutcome.NeedsReview, await _svc.CompleteAsync(order, default));
        Assert.Empty(_pc.Adjustments);
        Assert.Equal(PaymentState.Review, _store.Get(order)!.State);
    }

    [Fact]
    public async Task Papercut_down_keeps_order_retryable_then_credits_once()
    {
        var (order, checkout) = await StartAsync();
        _wl.Pay(checkout);
        _pc.FailNextAdjustment = new PaperCutNotAppliedException("down");

        Assert.Equal(CompletionOutcome.RetryLater, await _svc.CompleteAsync(order, default));
        Assert.Empty(_pc.Adjustments);

        Assert.Equal(CompletionOutcome.Credited, await _svc.CompleteAsync(order, default));
        Assert.Equal(CompletionOutcome.AlreadyCredited, await _svc.CompleteAsync(order, default));
        Assert.Single(_pc.Adjustments);
    }

    [Fact]
    public async Task Unknown_papercut_outcome_is_never_retried_automatically()
    {
        var (order, checkout) = await StartAsync();
        _wl.Pay(checkout);
        _pc.FailNextAdjustment = new PaperCutException("timeout");

        Assert.Equal(CompletionOutcome.NeedsReview, await _svc.CompleteAsync(order, default));
        Assert.Equal(CompletionOutcome.NeedsReview, await _svc.CompleteAsync(order, default));
        Assert.Empty(_pc.Adjustments);
        Assert.Equal(PaymentState.CreditUncertain, _store.Get(order)!.State);
    }

    [Fact]
    public async Task Unknown_order_is_not_found()
    {
        Assert.Equal(CompletionOutcome.NotFound, await _svc.CompleteAsync("PCDOESNOTEXIST", default));
    }

    [Fact]
    public async Task Amount_outside_offered_list_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.StartAsync("alice", 1000m, default));
    }

    [Fact]
    public async Task Unknown_user_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.StartAsync("mallory", 5m, default));
    }

    [Fact]
    public async Task Recent_orders_are_listed_newest_first()
    {
        var first = await _svc.StartAsync("alice", 5m, default);
        await Task.Delay(5);
        var second = await _svc.StartAsync("alice", 10m, default);

        var recent = _store.ListRecent(10);

        Assert.Equal([second.OrderId, first.OrderId], recent.Select(r => r.OrderId));
        Assert.Single(_store.ListRecent(1));
    }
}
