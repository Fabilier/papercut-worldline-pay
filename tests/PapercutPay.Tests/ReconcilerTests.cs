using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PapercutPay.PaperCut;
using PapercutPay.Payments;
using PapercutPay.Worldline;

namespace PapercutPay.Tests;

public sealed class ReconcilerTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"pcrec-{Guid.NewGuid():N}.db");
    private readonly InMemoryPaperCutClient _pc = new();
    private readonly FakeWorldlineClient _wl = new();
    private readonly SqlitePaymentStore _store;
    private readonly PaymentReconciler _reconciler;

    public ReconcilerTests()
    {
        _pc.SeedUser("alice", 0m);
        _store = new SqlitePaymentStore(_db);
        var opt = Options.Create(new AppOptions { Amounts = [5m, 10m] });
        var svc = new PaymentService(_store, _pc, _wl, opt, NullLogger<PaymentService>.Instance);
        _reconciler = new PaymentReconciler(svc, _store, opt, TimeProvider.System, NullLogger<PaymentReconciler>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
        {
            File.Delete(f);
        }
    }

    /// <summary>Inserts an order as if the customer had been sent to Worldline <paramref name="age"/> ago.</summary>
    private async Task<(string Order, string Checkout)> OpenOrderAsync(TimeSpan age, long amountMinor = 1000)
    {
        var order = "PC" + Guid.NewGuid().ToString("N")[..18].ToUpperInvariant();
        var created = await _wl.CreateHostedCheckoutAsync(order, amountMinor, "EUR", "http://x/return", "fr_FR", default);
        _store.Insert(new PaymentRecord(order, "alice", amountMinor, "EUR", PaymentState.Created, created.HostedCheckoutId, null, null, DateTime.UtcNow - age));
        return (order, created.HostedCheckoutId);
    }

    [Fact]
    public async Task Paid_order_whose_browser_never_returned_is_credited_once_across_passes()
    {
        var (order, checkout) = await OpenOrderAsync(TimeSpan.FromMinutes(10));
        _wl.Pay(checkout);

        Assert.Equal(1, await _reconciler.RunOnceAsync(default));
        Assert.Equal(0, await _reconciler.RunOnceAsync(default));

        Assert.Single(_pc.Adjustments);
        Assert.Equal(PaymentState.Credited, _store.Get(order)!.State);
    }

    [Fact]
    public async Task Recent_order_is_left_to_the_browser_return()
    {
        var (_, checkout) = await OpenOrderAsync(TimeSpan.FromSeconds(10));
        _wl.Pay(checkout);

        Assert.Equal(0, await _reconciler.RunOnceAsync(default));
        Assert.Empty(_pc.Adjustments);
    }

    [Fact]
    public async Task Order_older_than_the_window_is_ignored()
    {
        var (_, checkout) = await OpenOrderAsync(TimeSpan.FromHours(100));
        _wl.Pay(checkout);

        Assert.Equal(0, await _reconciler.RunOnceAsync(default));
        Assert.Empty(_pc.Adjustments);
    }

    [Fact]
    public async Task Unpaid_and_cancelled_orders_are_not_credited_and_cancelled_ones_stop_being_polled()
    {
        var (unpaid, _) = await OpenOrderAsync(TimeSpan.FromMinutes(10));
        var (cancelled, cancelledCheckout) = await OpenOrderAsync(TimeSpan.FromMinutes(10));
        _wl.Cancel(cancelledCheckout);

        Assert.Equal(0, await _reconciler.RunOnceAsync(default));

        Assert.Equal(PaymentState.Created, _store.Get(unpaid)!.State);
        Assert.Equal(PaymentState.Failed, _store.Get(cancelled)!.State);
        Assert.DoesNotContain(cancelled, _store.ListOpenOrderIds(DateTime.UtcNow, DateTime.UtcNow.AddDays(-1), 10));
        Assert.Empty(_pc.Adjustments);
    }

    [Fact]
    public async Task One_failing_order_does_not_block_the_others()
    {
        var (first, firstCheckout) = await OpenOrderAsync(TimeSpan.FromMinutes(20));
        var (second, secondCheckout) = await OpenOrderAsync(TimeSpan.FromMinutes(10));
        _wl.Pay(secondCheckout);

        // The first order points to a checkout Worldline does not know: the fake throws for it.
        _store.SetHostedCheckoutId(first, "unknown-checkout");

        Assert.Equal(1, await _reconciler.RunOnceAsync(default));
        Assert.Equal(PaymentState.Credited, _store.Get(second)!.State);
        Assert.Equal(PaymentState.Created, _store.Get(first)!.State);
        Assert.NotEmpty(firstCheckout);
    }

    [Fact]
    public async Task Order_credited_after_papercut_outage_on_a_later_pass()
    {
        var (order, checkout) = await OpenOrderAsync(TimeSpan.FromMinutes(10));
        _wl.Pay(checkout);
        _pc.FailNextAdjustment = new PaperCutNotAppliedException("down");

        Assert.Equal(0, await _reconciler.RunOnceAsync(default));
        Assert.Equal(PaymentState.Created, _store.Get(order)!.State);

        Assert.Equal(1, await _reconciler.RunOnceAsync(default));
        Assert.Single(_pc.Adjustments);
    }
}
