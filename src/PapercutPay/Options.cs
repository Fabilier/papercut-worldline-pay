namespace PapercutPay;

public sealed class AppOptions
{
    /// <summary>"Live" uses PaperCut and Worldline; "Fake" uses in-memory stand-ins for local tests.</summary>
    public string Mode { get; set; } = "Live";

    /// <summary>Public base URL of this service, including any virtual path (used to build the Worldline return URL). Required in Live mode.</summary>
    public string PublicBaseUrl { get; set; } = "";

    public string Currency { get; set; } = "EUR";

    /// <summary>Amounts offered to the user, in whole currency units. The posted amount must be one of these.</summary>
    public decimal[] Amounts { get; set; } = [];

    public string DbPath { get; set; } = "payments.db";

    /// <summary>
    /// Virtual path when the service is published under a sub-path behind something that does not strip it
    /// (for example a reverse proxy). Leave empty under IIS: the IIS module already provides the path base.
    /// </summary>
    public string PathBase { get; set; } = "";

    /// <summary>HMAC secret used to sign and verify user links (t= parameter).</summary>
    public string LinkSecret { get; set; } = "";

    /// <summary>
    /// Accept unsigned user links (the PaperCut custom link ?user=%user%). Anyone who knows a user name can then open the
    /// payment form for that account, so the balance is never shown for such links. Set to false to require signed links only.
    /// </summary>
    public bool AllowUnsignedLinks { get; set; } = true;

    /// <summary>Former name of <see cref="AllowUnsignedLinks"/>, kept so existing settings files keep working.</summary>
    public bool AllowLegacyBase64User
    {
        get => AllowUnsignedLinks;
        set => AllowUnsignedLinks = value;
    }

    /// <summary>Where the "Continue" button sends the user after payment (PaperCut user web).</summary>
    public string PaperCutUserUrl { get; set; } = "";

    /// <summary>Locale of the Worldline hosted payment page, for example en_GB or fr_FR.</summary>
    public string Locale { get; set; } = "en_GB";

    /// <summary>Language of this service's own pages: "en" or "fr".</summary>
    public string Language { get; set; } = "en";

    /// <summary>Name shown in the page header.</summary>
    public string BrandName { get; set; } = "PaperCut Pay";

    /// <summary>Accent color of the pages, as #rrggbb. Anything else falls back to the default.</summary>
    public string BrandColor { get; set; } = "#2b5c9e";

    /// <summary>Fake and Hybrid modes expose test endpoints and in-memory PaperCut. Refused outside Development unless this is true.</summary>
    public bool AllowFakeMode { get; set; }

    /// <summary>In Live mode, refuse plain-HTTP requests (except /health). Tokens in URLs must not travel in clear text.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Requests allowed per client address and minute.</summary>
    public int RateLimitPerMinute { get; set; } = 60;

    /// <summary>An order stuck in CREDITING longer than this is flagged for manual review instead of being left silent.</summary>
    public int StuckCreditMinutes { get; set; } = 10;

    /// <summary>How often the background check looks for paid orders whose browser return never arrived.</summary>
    public int ReconcileIntervalSeconds { get; set; } = 60;

    /// <summary>Orders younger than this are left to the normal browser return.</summary>
    public int ReconcileMinAgeSeconds { get; set; } = 120;

    /// <summary>Orders older than this are no longer checked.</summary>
    public int ReconcileMaxAgeHours { get; set; } = 72;
}

public sealed class PaperCutOptions
{
    /// <summary>Base URL of the PaperCut server, for example http://papercut.local:9191.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Authorization token for the XML-RPC Web Services API. Keep it out of source control.</summary>
    public string AuthToken { get; set; } = "";

    /// <summary>
    /// Optional PaperCut account to credit (for example the secondary personal account when multiple personal
    /// accounts are enabled). Empty means the user's primary account.
    /// </summary>
    public string AccountName { get; set; } = "";
}

public sealed class WorldlineOptions
{
    public string BaseUrl { get; set; } = "https://payment.preprod.direct.worldline-solutions.com";
    public string ApiVersion { get; set; } = "v2";
    public string MerchantId { get; set; } = "";
    public string ApiKeyId { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string WebhookKeyId { get; set; } = "";
    public string WebhookSecret { get; set; } = "";

    /// <summary>Payment statuses that trigger the PaperCut credit. Null means the built-in default.</summary>
    public string[]? PaidStatuses { get; set; }

    /// <summary>Payment statuses that are final failures. Null means the built-in default.</summary>
    public string[]? FailedStatuses { get; set; }

    public string[] EffectivePaidStatuses => PaidStatuses ?? ["CAPTURED", "PAID"];

    public string[] EffectiveFailedStatuses =>
        FailedStatuses ?? ["REJECTED", "REJECTED_CAPTURE", "CANCELLED", "REVERSED", "REFUNDED"];
}
