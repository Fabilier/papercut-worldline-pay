using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PapercutPay.Worldline;

/// <summary>
/// GCS v1HMAC request signing, as described in the Worldline Direct "manual authentication" guide:
/// string-to-sign = METHOD \n Content-Type \n Date \n [x-gcs-* headers, one per line] \n path \n,
/// signed with HMAC-SHA256 using the API secret, Base64-encoded.
/// </summary>
public static partial class GcsSigner
{
    public static string BuildStringToSign(
        string method,
        string contentType,
        string date,
        IReadOnlyDictionary<string, string> gcsHeaders,
        string path)
    {
        var sb = new StringBuilder();
        sb.Append(method.ToUpperInvariant()).Append('\n');
        sb.Append(contentType).Append('\n');
        sb.Append(date).Append('\n');
        foreach (var (name, value) in gcsHeaders
                     .Select(h => (Name: h.Key.ToLowerInvariant(), Value: Whitespace().Replace(h.Value.Trim(), " ")))
                     .OrderBy(h => h.Name, StringComparer.Ordinal))
        {
            sb.Append(name).Append(':').Append(value).Append('\n');
        }

        sb.Append(path).Append('\n');
        return sb.ToString();
    }

    public static string Sign(string stringToSign, string apiSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
    }

    public static string AuthorizationHeader(string apiKeyId, string signature) =>
        $"GCS v1HMAC:{apiKeyId}:{signature}";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
