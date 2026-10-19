namespace PapercutPay.PaperCut;

public interface IPaperCutClient
{
    Task<bool> UserExistsAsync(string userName, CancellationToken ct);
    Task<decimal> GetBalanceAsync(string userName, CancellationToken ct);

    /// <exception cref="PaperCutNotAppliedException">The adjustment was certainly not applied (safe to retry).</exception>
    /// <exception cref="PaperCutException">The outcome is unknown (do not retry blindly).</exception>
    Task AdjustBalanceAsync(string userName, decimal amount, string comment, CancellationToken ct);
}

public class PaperCutException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class PaperCutNotAppliedException(string message, Exception? inner = null) : PaperCutException(message, inner);
