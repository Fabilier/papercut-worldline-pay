using System.Collections.Concurrent;

namespace PapercutPay.PaperCut;

/// <summary>Stand-in used in Fake mode and tests. Users exist when they are seeded.</summary>
public sealed class InMemoryPaperCutClient : IPaperCutClient
{
    private readonly ConcurrentDictionary<string, decimal> _balances = new(StringComparer.OrdinalIgnoreCase);

    public List<(string User, decimal Amount, string Comment)> Adjustments { get; } = [];

    /// <summary>When set, the next adjustment throws it once.</summary>
    public Exception? FailNextAdjustment { get; set; }

    public void SeedUser(string userName, decimal balance = 0m) => _balances[userName] = balance;

    public Task<bool> UserExistsAsync(string userName, CancellationToken ct) =>
        Task.FromResult(_balances.ContainsKey(userName));

    public Task<decimal> GetBalanceAsync(string userName, CancellationToken ct) =>
        Task.FromResult(_balances.TryGetValue(userName, out var b) ? b : throw new PaperCutNotAppliedException("Unknown user."));

    public Task AdjustBalanceAsync(string userName, decimal amount, string comment, CancellationToken ct)
    {
        if (FailNextAdjustment is { } ex)
        {
            FailNextAdjustment = null;
            throw ex;
        }

        if (!_balances.ContainsKey(userName))
        {
            throw new PaperCutNotAppliedException("Unknown user.");
        }

        _balances.AddOrUpdate(userName, amount, (_, old) => old + amount);
        lock (Adjustments)
        {
            Adjustments.Add((userName, amount, comment));
        }

        return Task.CompletedTask;
    }
}
