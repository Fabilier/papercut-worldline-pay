using Microsoft.Data.Sqlite;

namespace PapercutPay.Payments;

public static class PaymentState
{
    public const string Created = "CREATED";
    public const string Crediting = "CREDITING";
    public const string Credited = "CREDITED";
    public const string Failed = "FAILED";

    /// <summary>Paid but amount, currency or reference did not match: needs a human decision.</summary>
    public const string Review = "REVIEW";

    /// <summary>Paid, and the PaperCut call ended with an unknown outcome: check the balance before retrying.</summary>
    public const string CreditUncertain = "CREDIT_UNCERTAIN";
}

public sealed record PaymentRecord(
    string OrderId,
    string UserName,
    long AmountMinor,
    string Currency,
    string State,
    string? HostedCheckoutId,
    string? PaymentId,
    string? Note,
    DateTime CreatedUtc);

public interface IPaymentStore
{
    void Insert(PaymentRecord record);
    void SetHostedCheckoutId(string orderId, string hostedCheckoutId);
    PaymentRecord? Get(string orderId);

    /// <summary>Atomically moves CREATED to CREDITING. Exactly one caller wins; this is the anti-double-credit guard.</summary>
    bool TryClaimForCredit(string orderId, string paymentId);

    void SetState(string orderId, string state, string? note = null);

    /// <summary>Orders that entered CREDITING before the given instant and never finished.</summary>
    IReadOnlyList<string> ListStuckCrediting(DateTime claimedBeforeUtc, int limit);

    /// <summary>The most recent orders, newest first (operator view).</summary>
    IReadOnlyList<PaymentRecord> ListRecent(int limit);

    /// <summary>Orders still CREATED with a Worldline checkout, created between the two instants (oldest first).</summary>
    IReadOnlyList<string> ListOpenOrderIds(DateTime createdBeforeUtc, DateTime createdAfterUtc, int limit);
}

public sealed class SqlitePaymentStore : IPaymentStore
{
    private readonly string _connectionString;

    public SqlitePaymentStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS payments (
                order_id TEXT PRIMARY KEY,
                user_name TEXT NOT NULL,
                amount_minor INTEGER NOT NULL,
                currency TEXT NOT NULL,
                state TEXT NOT NULL,
                hosted_checkout_id TEXT UNIQUE,
                payment_id TEXT,
                note TEXT,
                created_utc TEXT NOT NULL,
                claimed_utc TEXT
            );
            """);
        EnsureClaimedColumn(c);
    }

    public void Insert(PaymentRecord r)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO payments (order_id, user_name, amount_minor, currency, state, hosted_checkout_id, payment_id, note, created_utc)
            VALUES ($o, $u, $a, $c, $s, $h, $p, $n, $t);
            """;
        cmd.Parameters.AddWithValue("$o", r.OrderId);
        cmd.Parameters.AddWithValue("$u", r.UserName);
        cmd.Parameters.AddWithValue("$a", r.AmountMinor);
        cmd.Parameters.AddWithValue("$c", r.Currency);
        cmd.Parameters.AddWithValue("$s", r.State);
        cmd.Parameters.AddWithValue("$h", (object?)r.HostedCheckoutId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (object?)r.PaymentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)r.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", r.CreatedUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void SetHostedCheckoutId(string orderId, string hostedCheckoutId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE payments SET hosted_checkout_id = $h WHERE order_id = $o;";
        cmd.Parameters.AddWithValue("$h", hostedCheckoutId);
        cmd.Parameters.AddWithValue("$o", orderId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PaymentRecord> ListRecent(int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT order_id FROM payments ORDER BY created_utc DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        var ids = new List<string>();
        using (var rd = cmd.ExecuteReader())
        {
            while (rd.Read())
            {
                ids.Add(rd.GetString(0));
            }
        }

        return ids.Select(Get).OfType<PaymentRecord>().ToList();
    }

    public PaymentRecord? Get(string orderId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT order_id, user_name, amount_minor, currency, state, hosted_checkout_id, payment_id, note, created_utc
            FROM payments WHERE order_id = $o;
            """;
        cmd.Parameters.AddWithValue("$o", orderId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read())
        {
            return null;
        }

        return new PaymentRecord(
            rd.GetString(0), rd.GetString(1), rd.GetInt64(2), rd.GetString(3), rd.GetString(4),
            rd.IsDBNull(5) ? null : rd.GetString(5),
            rd.IsDBNull(6) ? null : rd.GetString(6),
            rd.IsDBNull(7) ? null : rd.GetString(7),
            DateTime.Parse(rd.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public bool TryClaimForCredit(string orderId, string paymentId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE payments SET state = $new, payment_id = $p, claimed_utc = $t WHERE order_id = $o AND state = $old;";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$new", PaymentState.Crediting);
        cmd.Parameters.AddWithValue("$p", paymentId);
        cmd.Parameters.AddWithValue("$o", orderId);
        cmd.Parameters.AddWithValue("$old", PaymentState.Created);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void SetState(string orderId, string state, string? note = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE payments SET state = $s, note = COALESCE($n, note) WHERE order_id = $o;";
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$o", orderId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> ListOpenOrderIds(DateTime createdBeforeUtc, DateTime createdAfterUtc, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT order_id FROM payments
            WHERE state = $s AND hosted_checkout_id IS NOT NULL AND created_utc <= $before AND created_utc >= $after
            ORDER BY created_utc LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$s", PaymentState.Created);
        cmd.Parameters.AddWithValue("$before", createdBeforeUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$after", createdAfterUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var rd = cmd.ExecuteReader();
        var ids = new List<string>();
        while (rd.Read())
        {
            ids.Add(rd.GetString(0));
        }

        return ids;
    }

    public IReadOnlyList<string> ListStuckCrediting(DateTime claimedBeforeUtc, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT order_id FROM payments
            WHERE state = $s AND (claimed_utc IS NULL OR claimed_utc <= $before)
            ORDER BY created_utc LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$s", PaymentState.Crediting);
        cmd.Parameters.AddWithValue("$before", claimedBeforeUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var rd = cmd.ExecuteReader();
        var ids = new List<string>();
        while (rd.Read())
        {
            ids.Add(rd.GetString(0));
        }

        return ids;
    }

    /// <summary>Databases created by an earlier version lack the claim timestamp.</summary>
    private static void EnsureClaimedColumn(SqliteConnection c)
    {
        using var info = c.CreateCommand();
        info.CommandText = "PRAGMA table_info(payments);";
        var hasColumn = false;
        using (var rd = info.ExecuteReader())
        {
            while (rd.Read())
            {
                hasColumn |= rd.GetString(1) == "claimed_utc";
            }
        }

        if (!hasColumn)
        {
            Exec(c, "ALTER TABLE payments ADD COLUMN claimed_utc TEXT;");
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
