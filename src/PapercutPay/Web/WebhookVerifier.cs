using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PapercutPay.Web;

public static class WebhookVerifier
{
    /// <summary>
    /// Checks X-GCS-Signature = Base64(HMAC-SHA256(rawBody, webhookSecret)) in constant time.
    /// Header names and scheme come from the Worldline webhooks guide; confirm them in pre-production.
    /// </summary>
    public static bool IsValid(byte[] rawBody, string? signatureHeader, string? keyIdHeader, string expectedKeyId, string secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader) || keyIdHeader != expectedKeyId)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Encoding.ASCII.GetBytes(Convert.ToBase64String(hmac.ComputeHash(rawBody)));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signatureHeader));
    }

    /// <summary>Finds the first "merchantReference" anywhere in the payload. Used only as a hint to look up our own order.</summary>
    public static string? FindMerchantReference(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["merchantReference"] is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    return s;
                }

                return obj.Select(p => FindMerchantReference(p.Value)).FirstOrDefault(r => r is not null);
            case JsonArray arr:
                return arr.Select(FindMerchantReference).FirstOrDefault(r => r is not null);
            default:
                return null;
        }
    }
}
