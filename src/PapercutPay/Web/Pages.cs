using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using PapercutPay.Payments;

namespace PapercutPay.Web;

/// <summary>
/// Server-rendered pages. Every dynamic value goes through HtmlEncode. No script and no external resource, so the
/// strict Content-Security-Policy (default-src 'none', inline styles only) keeps working.
/// </summary>
public static partial class Pages
{
    private const string DefaultBrandName = "PaperCut Pay";
    private const string DefaultBrandColor = "#2b5c9e";

    public static string BrandName { get; private set; } = DefaultBrandName;

    public static string BrandColor { get; private set; } = DefaultBrandColor;

    /// <summary>Applies language and branding once at startup. The color is validated so configuration cannot inject CSS.</summary>
    public static void Configure(AppOptions options)
    {
        Strings.Configure(options.Language);
        BrandName = string.IsNullOrWhiteSpace(options.BrandName) ? DefaultBrandName : options.BrandName.Trim();
        BrandColor = HexColor().IsMatch(options.BrandColor ?? "") ? options.BrandColor! : DefaultBrandColor;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Style = """
        :root{--brand:__BRAND__;--brand-dark:color-mix(in srgb,var(--brand) 80%,#000);--bg:#eef0f6;--card:#fff;--text:#1d2233;--muted:#5d6479;--line:#dfe2ec;
          --ok:#0e7a4f;--ok-bg:#e4f5ec;--warn:#8a5a00;--warn-bg:#fff3d6;--err:#a12c2c;--err-bg:#fde8e8;--neg:#a12c2c}
        @media (prefers-color-scheme:dark){
          :root{--bg:#14161f;--card:#1e2130;--text:#eceef6;--muted:#a3a9bd;--line:#31354a;
            --ok:#5fd6a0;--ok-bg:#16352a;--warn:#f0c36a;--warn-bg:#3a2e12;--err:#ff9a9a;--err-bg:#3d1b1b;--neg:#ff9a9a}
        }
        *{box-sizing:border-box}
        body{margin:0;min-height:100vh;display:flex;flex-direction:column;background:var(--bg);color:var(--text);
          font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;line-height:1.45}
        .top{background:var(--brand);color:#fff;padding:16px 20px}
        .brand{max-width:480px;margin:0 auto;display:flex;align-items:center;gap:12px;font-weight:700;font-size:1.15rem;letter-spacing:.2px}
        .mark{display:inline-grid;place-items:center;width:34px;height:34px;border-radius:9px;background:#fff;color:var(--brand);font-size:1.2rem}
        .sub{font-weight:400;opacity:.8;margin-left:auto;font-size:.95rem}
        main{flex:1;width:100%;max-width:480px;margin:0 auto;padding:22px 16px 8px}
        .card{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:24px;box-shadow:0 6px 24px rgba(30,34,60,.08)}
        h1{font-size:1.3rem;margin:0 0 18px}
        .user{display:flex;align-items:center;gap:14px;margin-bottom:18px}
        .avatar{flex:none;display:grid;place-items:center;width:48px;height:48px;border-radius:50%;background:var(--brand);color:#fff;font-weight:700;font-size:1.2rem}
        .name{font-weight:600;word-break:break-all}.role{color:var(--muted);font-size:.9rem}
        .balance{display:flex;justify-content:space-between;align-items:baseline;background:var(--bg);border-radius:12px;padding:12px 16px;margin-bottom:22px}
        .balance span{color:var(--muted)}.balance strong{font-size:1.25rem}.balance strong.neg{color:var(--neg)}
        fieldset{border:0;padding:0;margin:0 0 20px}
        legend{font-weight:600;margin-bottom:10px;padding:0}
        .amounts{display:grid;grid-template-columns:repeat(auto-fit,minmax(88px,1fr));gap:10px}
        .amt{position:relative}
        .amt input{position:absolute;opacity:0;inset:0;width:100%;height:100%;margin:0;cursor:pointer}
        .amt label{display:block;text-align:center;padding:14px 8px;border:2px solid var(--line);border-radius:12px;font-weight:700;font-size:1.1rem;background:var(--card)}
        .amt input:checked + label{border-color:var(--brand);background:var(--brand);color:#fff}
        .amt input:focus-visible + label{outline:3px solid color-mix(in srgb,var(--brand) 55%,#fff);outline-offset:2px}
        .pay,a.btn{display:flex;align-items:center;justify-content:center;gap:10px;width:100%;padding:15px 18px;border:0;border-radius:12px;
          background:var(--brand);color:#fff;font-size:1.05rem;font-weight:700;cursor:pointer;text-decoration:none}
        .pay:hover,a.btn:hover{background:var(--brand-dark)}
        .secure{display:flex;gap:8px;align-items:flex-start;color:var(--muted);font-size:.85rem;margin:16px 0 0}
        .secure svg{flex:none;margin-top:2px}
        .msg{padding:12px 14px;border-radius:10px;margin-bottom:18px}
        .msg.err{background:var(--err-bg);color:var(--err)}
        .status{text-align:center}
        .icon{display:grid;place-items:center;width:72px;height:72px;border-radius:50%;margin:4px auto 16px}
        .icon.ok{background:var(--ok-bg);color:var(--ok)}.icon.warn{background:var(--warn-bg);color:var(--warn)}.icon.err{background:var(--err-bg);color:var(--err)}
        .status h1{margin-bottom:8px}.status p{margin:0 0 14px;color:var(--muted)}
        .ref{display:inline-block;background:var(--bg);border-radius:8px;padding:6px 10px;margin:4px 0 18px;font-family:ui-monospace,Consolas,monospace;font-size:.85rem;color:var(--text)}
        footer{text-align:center;color:var(--muted);font-size:.8rem;padding:14px 16px 22px}
        """;

    private static string Layout(string title, string body, int refreshSeconds = 0) => $$"""
        <!doctype html>
        <html lang="{{Strings.Language}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        {{(refreshSeconds > 0 ? $"<meta http-equiv=\"refresh\" content=\"{refreshSeconds}\">" : "")}}
        <title>{{E(title)}}</title>
        <style>{{Style.Replace("__BRAND__", BrandColor)}}</style></head><body>
        <header class="top"><div class="brand"><span class="mark">{{E(BrandName[..1].ToUpperInvariant())}}</span>{{E(BrandName)}}<span class="sub">{{E(Strings.T("subtitle"))}}</span></div></header>
        <main>{{body}}</main>
        <footer>{{E(Strings.T("footer"))}}</footer>
        </body></html>
        """;

    private const string Lock =
        "<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><rect x=\"4\" y=\"11\" width=\"16\" height=\"10\" rx=\"2\"/><path d=\"M8 11V8a4 4 0 0 1 8 0v3\"/></svg>";

    private const string CardIcon =
        "<svg width=\"22\" height=\"22\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><rect x=\"2\" y=\"5\" width=\"20\" height=\"14\" rx=\"3\"/><path d=\"M2 10h20\"/></svg>";

    public static string PaymentForm(string userName, decimal? balance, string currency, IEnumerable<decimal> amounts, string credField, string credValue, string pathBase, string? error = null)
    {
        var list = amounts.ToList();
        var choices = string.Join("", list.Select((a, i) =>
            $"<div class=\"amt\"><input type=\"radio\" name=\"amount\" id=\"a{i}\" value=\"{a.ToString(CultureInfo.InvariantCulture)}\"{(i == 0 ? " checked" : "")}><label for=\"a{i}\">{E(Money(a, currency, compact: true))}</label></div>"));
        var err = error is null ? "" : $"<div class=\"msg err\" role=\"alert\">{E(error)}</div>";
        var initial = E(userName.Length > 0 ? char.ToUpperInvariant(userName[0]).ToString() : "?");
        var balanceRow = balance is { } b
            ? $"<div class=\"balance\"><span>{E(Strings.T("balance"))}</span><strong class=\"{(b < 0 ? "neg" : "")}\">{E(Money(b, currency))}</strong></div>"
            : "";

        return Layout(Strings.T("page_title"), $"""
            <div class="card">
              <h1>{E(Strings.T("top_up_title"))}</h1>{err}
              <div class="user"><span class="avatar" aria-hidden="true">{initial}</span><div><div class="name">{E(userName)}</div><div class="role">{E(Strings.T("account"))}</div></div></div>
              {balanceRow}
              <form method="post" action="{E(pathBase)}/pay/start">
                <input type="hidden" name="{E(credField)}" value="{E(credValue)}">
                <fieldset><legend>{E(Strings.T("amount"))}</legend><div class="amounts">{choices}</div></fieldset>
                <button class="pay" type="submit">{CardIcon}{E(Strings.T("pay"))}</button>
              </form>
              <p class="secure">{Lock}<span>{E(Strings.T("secure_note"))}</span></p>
            </div>
            """);
    }

    public static string Result(CompletionOutcome outcome, string? continueUrl, string? orderId)
    {
        var (css, key, refresh) = outcome switch
        {
            CompletionOutcome.Credited => ("ok", "credited", 0),
            CompletionOutcome.AlreadyCredited => ("ok", "already", 0),
            CompletionOutcome.Pending => ("warn", "pending", 10),
            CompletionOutcome.RetryLater => ("warn", "retry", 15),
            CompletionOutcome.Failed => ("err", "failed", 0),
            CompletionOutcome.NeedsReview => ("warn", "review", 0),
            _ => ("err", "notfound", 0),
        };
        var title = Strings.T(key + "_title");
        var text = Strings.T(key + "_text");
        var icon = css switch
        {
            "ok" => "<svg width=\"38\" height=\"38\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M5 13l4 4L19 7\"/></svg>",
            "err" => "<svg width=\"34\" height=\"34\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M6 6l12 12M18 6L6 18\"/></svg>",
            _ => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.4\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7v5l3 2\"/></svg>",
        };
        var reference = string.IsNullOrEmpty(orderId) ? "" : $"<div>{E(Strings.T("reference"))}</div><div class=\"ref\">{E(orderId)}</div>";
        var link = string.IsNullOrEmpty(continueUrl) ? "" : $"<a class=\"btn\" href=\"{E(continueUrl)}\">{E(Strings.T("continue"))}</a>";
        return Layout(title, $"""
            <div class="card status">
              <div class="icon {css}" role="img" aria-label="{E(title)}">{icon}</div>
              <h1>{E(title)}</h1>
              <p>{E(text)}</p>
              {reference}
              {link}
            </div>
            """, refresh);
    }

    public static string Error(string message) => Layout(Strings.T("error_title"), $"""
        <div class="card status">
          <div class="icon err" role="img" aria-label="{E(Strings.T("error_title"))}"><svg width="34" height="34" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg></div>
          <h1>{E(Strings.T("error_title"))}</h1>
          <p>{E(message)}</p>
        </div>
        """);

    public static string FakeCheckout(string id, string reference, string pathBase) => Layout("Worldline simulation", $"""
        <div class="card">
          <h1>Worldline payment page simulation</h1>
          <p>Reference {E(reference)}. Test mode only.</p>
          <form method="post" action="{E(pathBase)}/dev/fake-checkout/{E(id)}">
            <button class="pay" name="action" value="pay">Simulate a successful payment</button>
            <button class="pay" name="action" value="cancel" style="background:#8a2c2c;margin-top:10px">Simulate a cancellation</button>
          </form>
        </div>
        """);

    private static string Money(decimal amount, string currency, bool compact = false)
    {
        var symbol = currency.Equals("EUR", StringComparison.OrdinalIgnoreCase) ? "€" : currency;
        var number = Math.Abs(amount).ToString(compact && amount % 1 == 0 ? "N0" : "N2", Strings.Culture);
        var sign = amount < 0 ? "-" : "";
        return Strings.IsFrench ? $"{sign}{number}\u00a0{symbol}" : $"{sign}{symbol}{number}";
    }
}
