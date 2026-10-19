using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using PapercutPay.PaperCut;
using PapercutPay.Payments;
using PapercutPay.Web;
using PapercutPay.Worldline;

namespace PapercutPay.Tests;

public class UserLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_token_returns_user()
    {
        var t = UserLink.Create("alice", Now.AddMinutes(10), "s3cret");
        Assert.Equal("alice", UserLink.TryReadUser(t, "s3cret", Now));
    }

    [Fact]
    public void User_with_pipe_and_unicode_round_trips()
    {
        var t = UserLink.Create("élève|x", Now.AddMinutes(10), "s3cret");
        Assert.Equal("élève|x", UserLink.TryReadUser(t, "s3cret", Now));
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var t = UserLink.Create("alice", Now.AddMinutes(-1), "s3cret");
        Assert.Null(UserLink.TryReadUser(t, "s3cret", Now));
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var t = UserLink.Create("alice", Now.AddMinutes(10), "s3cret");
        Assert.Null(UserLink.TryReadUser(t, "other", Now));
    }

    [Fact]
    public void Tampered_user_is_rejected()
    {
        var t = UserLink.Create("alice", Now.AddMinutes(10), "s3cret");
        var forged = UserLink.Create("bob", Now.AddMinutes(10), "attacker").Split('.')[0] + "." + t.Split('.')[1];
        Assert.Null(UserLink.TryReadUser(forged, "s3cret", Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    public void Garbage_is_rejected(string? token) => Assert.Null(UserLink.TryReadUser(token, "s3cret", Now));

    [Theory]
    [InlineData("?dXNlcj1qYW5lLmRvZUBleGFtcGxlLm9yZw==")]
    [InlineData("dXNlcj1qYW5lLmRvZUBleGFtcGxlLm9yZw==")]
    [InlineData("?dXNlcj1qYW5lLmRvZUBleGFtcGxlLm9yZw%3D%3D")]
    public void Link_sent_by_papercut_is_read_as_base64_of_user_equals_name(string query) =>
        Assert.Equal("jane.doe@example.org", UserLink.TryReadLegacyQuery(query));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?user=abc")]
    [InlineData("?%%%")]
    public void Anything_else_is_not_a_papercut_link(string? query) => Assert.Null(UserLink.TryReadLegacyQuery(query));

    [Fact]
    public void Papercut_link_carrying_another_key_is_rejected()
    {
        var other = Convert.ToBase64String("admin=root"u8.ToArray());
        Assert.Null(UserLink.TryReadLegacyQuery("?" + other));
    }

    [Theory]
    [InlineData("jane.doe@example.org", "jane.doe@example.org")]
    [InlineData("alice", "alice")]
    public void Plain_user_from_the_papercut_link_is_accepted(string value, string expected) =>
        Assert.Equal(expected, UserLink.TryReadPlainUser(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    public void Empty_or_control_characters_are_not_a_user(string? value) => Assert.Null(UserLink.TryReadPlainUser(value));

    [Fact]
    public void Overlong_user_is_rejected() => Assert.Null(UserLink.TryReadPlainUser(new string('a', 129)));
}

public class GcsSignerTests
{
    [Fact]
    public void String_to_sign_matches_documented_layout_for_post()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-GCS-Idempotency-Key"] = "abc-def-456",
            ["x-gcs-date"] = "Mon, 18 May 2026 10:00:00 GMT",
        };

        var s = GcsSigner.BuildStringToSign("post", "application/json", "Mon, 18 May 2026 10:00:00 GMT", headers, "/v1/m/payments");

        Assert.Equal(
            "POST\napplication/json\nMon, 18 May 2026 10:00:00 GMT\nx-gcs-date:Mon, 18 May 2026 10:00:00 GMT\nx-gcs-idempotency-key:abc-def-456\n/v1/m/payments\n",
            s);
    }

    [Fact]
    public void String_to_sign_for_get_has_empty_content_type_line()
    {
        var s = GcsSigner.BuildStringToSign("GET", "", "Mon, 18 May 2026 10:00:00 GMT", new Dictionary<string, string>(), "/v2/m/hostedcheckouts/x");
        Assert.Equal("GET\n\nMon, 18 May 2026 10:00:00 GMT\n/v2/m/hostedcheckouts/x\n", s);
    }

    [Fact]
    public void Signature_is_base64_hmac_sha256()
    {
        var expected = Convert.ToBase64String(HMACSHA256.HashData("secret"u8, "payload"u8));
        Assert.Equal(expected, GcsSigner.Sign("payload", "secret"));
        Assert.Equal("GCS v1HMAC:key:sig", GcsSigner.AuthorizationHeader("key", "sig"));
    }
}

public class WebhookVerifierTests
{
    private static string SignBody(byte[] body, string secret) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void Valid_signature_is_accepted()
    {
        var body = """{"payment":{"paymentOutput":{"references":{"merchantReference":"PC1"}}}}"""u8.ToArray();
        Assert.True(WebhookVerifier.IsValid(body, SignBody(body, "whsec"), "k1", "k1", "whsec"));
    }

    [Theory]
    [InlineData("whsec", "k1", "k2")]
    [InlineData("wrong", "k1", "k1")]
    public void Bad_secret_or_key_is_rejected(string secret, string keyHeader, string expectedKey)
    {
        var body = "{}"u8.ToArray();
        Assert.False(WebhookVerifier.IsValid(body, SignBody(body, "whsec"), keyHeader, expectedKey, secret));
    }

    [Fact]
    public void Tampered_body_or_missing_signature_is_rejected()
    {
        var body = "{}"u8.ToArray();
        var sig = SignBody(body, "whsec");
        Assert.False(WebhookVerifier.IsValid("{ }"u8.ToArray(), sig, "k", "k", "whsec"));
        Assert.False(WebhookVerifier.IsValid(body, null, "k", "k", "whsec"));
        Assert.False(WebhookVerifier.IsValid(body, sig, "k", "k", ""));
    }

    [Fact]
    public void Merchant_reference_is_found_in_nested_payload()
    {
        var json = JsonNode.Parse("""{"id":"e","payment":{"paymentOutput":{"references":{"merchantReference":"PC9"}}}}""");
        Assert.Equal("PC9", WebhookVerifier.FindMerchantReference(json));
        Assert.Null(WebhookVerifier.FindMerchantReference(JsonNode.Parse("""{"a":[1,2]}""")));
    }
}

public class WorldlineMappingTests
{
    private static readonly WorldlineOptions Opt = new();

    [Theory]
    [InlineData("PAYMENT_CREATED", "CAPTURED", CheckoutVerdict.Paid)]
    [InlineData("PAYMENT_CREATED", "PAID", CheckoutVerdict.Paid)]
    [InlineData("PAYMENT_CREATED", "REJECTED", CheckoutVerdict.Failed)]
    [InlineData("PAYMENT_CREATED", "PENDING_CAPTURE", CheckoutVerdict.Pending)]
    [InlineData("IN_PROGRESS", null, CheckoutVerdict.Pending)]
    [InlineData("CANCELLED_BY_CONSUMER", null, CheckoutVerdict.Failed)]
    public void Status_is_classified(string hosted, string? payment, CheckoutVerdict expected) =>
        Assert.Equal(expected, WorldlineClient.Classify(hosted, payment, Opt));

    [Fact]
    public void Paid_response_is_parsed()
    {
        var json = JsonNode.Parse("""
            {"status":"PAYMENT_CREATED","createdPaymentOutput":{"payment":{"id":"123_0","status":"CAPTURED",
             "paymentOutput":{"amountOfMoney":{"amount":1000,"currencyCode":"EUR"},"references":{"merchantReference":"PC1"}}}}}
            """)!;

        var s = WorldlineClient.ParseStatus(json, Opt);

        Assert.Equal(CheckoutVerdict.Paid, s.Verdict);
        Assert.Equal(("123_0", 1000L, "EUR", "PC1"), (s.PaymentId, s.AmountMinor, s.Currency, s.MerchantReference));
    }
}

public class PaperCutXmlRpcTests
{
    [Fact]
    public void Adjust_request_is_well_formed_with_escaped_values_and_invariant_number()
    {
        var xml = XmlRpcPaperCutClient.BuildRequest("api.adjustUserAccountBalance",
            [new XElement("string", "tok"), new XElement("string", "a<b&c"), new XElement("double", "10.00"), new XElement("string", "c")]);

        var doc = XDocument.Parse(xml);
        Assert.Equal("api.adjustUserAccountBalance", doc.Root!.Element("methodName")!.Value);
        var values = doc.Root.Element("params")!.Elements("param").Select(p => p.Element("value")!.Elements().Single().Value).ToArray();
        Assert.Equal(["tok", "a<b&c", "10.00", "c"], values);
    }

    [Fact]
    public void Adjust_result_true_is_success_false_is_not_applied_other_is_uncertain()
    {
        XmlRpcPaperCutClient.EnsureSuccess(new XElement("value", new XElement("boolean", "1")));
        Assert.Throws<PaperCutNotAppliedException>(() =>
            XmlRpcPaperCutClient.EnsureSuccess(new XElement("value", new XElement("boolean", "0"))));
        var ex = Assert.Throws<PaperCutException>(() => XmlRpcPaperCutClient.EnsureSuccess(new XElement("value")));
        Assert.IsNotType<PaperCutNotAppliedException>(ex);
    }
}
