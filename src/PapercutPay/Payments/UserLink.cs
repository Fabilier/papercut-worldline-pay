using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace PapercutPay.Payments;

/// <summary>
/// Signed, expiring user link: t = base64url("user|expiryUnix") + "." + base64url(HMAC-SHA256).
/// Replaces the unsigned Base64 user parameter so the account to credit cannot be edited in the URL.
/// </summary>
public static class UserLink
{
    public static string Create(string userName, DateTimeOffset expires, string secret)
    {
        var payload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes($"{userName}|{expires.ToUnixTimeSeconds()}"));
        return $"{payload}.{Sign(payload, secret)}";
    }

    public static string? TryReadUser(string? token, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(parts[0], secret));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[1])))
        {
            return null;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64UrlTextEncoder.Decode(parts[0]));
        }
        catch (FormatException)
        {
            return null;
        }

        var sep = text.LastIndexOf('|');
        if (sep <= 0 || !long.TryParse(text[(sep + 1)..], out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp) < now)
        {
            return null;
        }

        return text[..sep];
    }

    /// <summary>
    /// The link PaperCut builds from its custom menu entry (...?user=%user%): the plain user name.
    /// Unsigned, so it is only as trustworthy as the person holding the URL.
    /// </summary>
    public static string? TryReadPlainUser(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl) ? value : null;

    /// <summary>
    /// Alternative unsigned link format: the whole query string is Base64 of "user=NAME"
    /// (for example ?dXNlcj1qYW5lLmRvZUBleGFtcGxlLm9yZw== for user=jane.doe@example.org). Unsigned.
    /// </summary>
    public static string? TryReadLegacyQuery(string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return null;
        }

        try
        {
            var encoded = Uri.UnescapeDataString(rawQuery.TrimStart('?')).Replace(' ', '+');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            const string prefix = "user=";
            if (!decoded.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            var name = decoded[prefix.Length..].Split('&')[0];
            return name.Length is > 0 and <= 128 && !name.Any(char.IsControl) ? name : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Base64UrlTextEncoder.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(payload)));
    }
}
