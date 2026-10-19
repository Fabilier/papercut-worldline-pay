using PapercutPay.Payments;
using PapercutPay.Web;

namespace PapercutPay.Tests;

[Collection("EnvironmentBound")]
public sealed class BrandingTests : IDisposable
{
    public void Dispose() => Pages.Configure(new AppOptions());

    [Fact]
    public void Every_text_exists_in_both_languages()
    {
        foreach (var key in Strings.Keys)
        {
            var (fr, en) = Strings.Both(key);
            Assert.False(string.IsNullOrWhiteSpace(fr), $"{key} has no French text");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has no English text");
        }
    }

    [Fact]
    public void Every_payment_outcome_has_a_title_and_a_text()
    {
        foreach (var outcome in Enum.GetValues<CompletionOutcome>())
        {
            var html = Pages.Result(outcome, null, "PC1");
            Assert.DoesNotContain("_title", html);
            Assert.DoesNotContain("_text", html);
        }
    }

    [Fact]
    public void Brand_name_and_color_are_applied_and_the_name_is_encoded()
    {
        Pages.Configure(new AppOptions { BrandName = "Acme <b>Print</b>", BrandColor = "#112233" });

        var html = Pages.Error("x");

        Assert.Contains("--brand:#112233", html);
        Assert.Contains("Acme &lt;b&gt;Print&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Print", html);
        Assert.Contains("<span class=\"mark\">A</span>", html);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#112233;}</style><script>alert(1)</script>")]
    [InlineData("")]
    public void Invalid_color_falls_back_to_the_default_and_cannot_inject_css(string color)
    {
        Pages.Configure(new AppOptions { BrandColor = color });

        var html = Pages.Error("x");

        Assert.Contains("--brand:#2b5c9e", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Blank_brand_name_uses_the_default()
    {
        Pages.Configure(new AppOptions { BrandName = "   " });
        Assert.Contains("PaperCut Pay", Pages.Error("x"));
    }

    [Fact]
    public void French_pages_use_french_texts_and_amounts()
    {
        Pages.Configure(new AppOptions { Language = "fr" });

        // Pages HTML-encode the non-breaking space; decode so the assertions read the text the user sees.
        var html = System.Net.WebUtility.HtmlDecode(Pages.PaymentForm("jane", -1163.10m, "EUR", [5m, 13.02m], "user", "jane", ""));

        Assert.Contains("lang=\"fr\"", html);
        Assert.Contains("Solde actuel", html);
        Assert.Contains("Payer par carte", html);
        Assert.Contains("5\u00a0€", html);
        Assert.Contains("13,02\u00a0€", html);
        // The thousands separator differs between ICU and Windows culture data (U+202F or U+00A0).
        Assert.Matches(@"-1[\s\u00a0\u202f]163,10\u00a0€", html);
    }

    [Fact]
    public void English_pages_use_english_texts_and_amounts()
    {
        Pages.Configure(new AppOptions { Language = "en" });

        var html = Pages.PaymentForm("jane", 1163.10m, "EUR", [5m, 13.02m], "user", "jane", "");

        Assert.Contains("lang=\"en\"", html);
        Assert.Contains("Current balance", html);
        Assert.Contains("Pay by card", html);
        Assert.Contains("€5", html);
        Assert.Contains("€13.02", html);
        Assert.Contains("€1,163.10", html);
    }

    [Fact]
    public void Unknown_language_falls_back_to_english()
    {
        Pages.Configure(new AppOptions { Language = "de" });
        Assert.Contains("Pay by card", Pages.PaymentForm("jane", 1m, "EUR", [5m], "user", "jane", ""));
    }

    [Fact]
    public void Other_currencies_show_their_code()
    {
        Pages.Configure(new AppOptions { Language = "en" });
        Assert.Contains("CHF10", Pages.PaymentForm("jane", 1m, "CHF", [10m], "user", "jane", ""));
    }
}
