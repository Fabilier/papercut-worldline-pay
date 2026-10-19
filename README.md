# PapercutPay

> English | [Français](README.fr.md)

Let users top up their **PaperCut MF** print credit by card, through **Worldline Direct Hosted Checkout**.

PapercutPay is a small ASP.NET Core 8 service that sits between the PaperCut user web and Worldline's hosted payment page. It was written to replace legacy Ogone/Ingenico "Standard" hosted-payment-page connectors, whose long-term availability is not guaranteed, while keeping the same user journey.

> **Status.** Exercised against the Worldline Direct **pre-production** API (October 2026) and a real PaperCut MF server: successful payment, cancellation, refusal and page reload with no double credit; PaperCut balance read and adjusted. The background recovery of a lost browser return was verified with the simulated payment page, not yet with a real Worldline payment. It is not affiliated with, endorsed by or supported by Worldline or PaperCut. This software moves money: read the [security notes](SECURITY.md), test in pre-production, and review the code before using it in production.

## How it works

```
 PaperCut user web                     PapercutPay                          Worldline
        |  custom link ?user=%user%        |                                     |
        |--------------------------------->|  form: choose an amount             |
        |                                  |-- CreateHostedCheckout ------------>|
        |<-------- redirect to hosted payment page ------------------------------|
        |           (customer pays on Worldline's page)                          |
        |--------------------------------->|  GET /pay/return?o=<order>          |
        |                                  |-- GetHostedCheckout --------------->|  status read server to server
        |                                  |-- adjustUserAccountBalance -> PaperCut (once, after confirmation)
        |<---------- "Payment accepted" ---|
```

Card data never reaches this service: the customer pays on Worldline's hosted page.

## Guarantees

- **Credit only after confirmation.** The PaperCut account is credited only when Worldline itself reports the payment as captured, with the same amount, currency and merchant reference as the stored order. Nothing in the return URL is trusted.
- **Never twice.** Browser return, background check and webhook can race; an atomic database claim makes exactly one of them credit the account.
- **Safe failure modes.** If PaperCut is unreachable, the order stays retryable. If the outcome of the PaperCut call is unknown (timeout), the order is flagged `CREDIT_UNCERTAIN` and is never replayed automatically.
- **Lost browser returns are recovered.** A background check re-verifies open orders every minute, so a customer who closes the tab after paying still gets credited.
- **Amounts are server-side.** The posted amount must be one of the configured amounts.
- **Hardened pages.** HTTPS enforced in Live mode, per-address rate limiting, strict security headers, no script and no external resource.

## Requirements

- .NET 8 SDK to build, or the ASP.NET Core 8 runtime to run (the Hosting Bundle on IIS).
- A PaperCut MF server with the [Web Services API](https://www.papercut.com/help/manuals/ng-mf/common/tools-web-services/) enabled: an authentication token and the address of this service in the allowed addresses.
- A Worldline Direct merchant account with an API key and secret (Merchant Portal, Developer, Payment API).

## Quick start (no PaperCut, no Worldline needed)

Fake mode uses an in-memory PaperCut with one user (`demo`) and a simulated payment page:

```bash
dotnet run --project src/PapercutPay -- --App:Mode=Fake --App:LinkSecret=dev --App:DbPath=demo.db
```

Open the address printed in the console ("Now listening on: ...") followed by `/dev/link/demo`, choose an amount, and use the simulated payment page. Fake mode is refused outside the `Development` environment unless `App:AllowFakeMode=true`, because it exposes endpoints that mint payment links.

Run the tests:

```bash
dotnet test
```

## Configuration

Settings are read, in increasing priority, from `appsettings.json`, an optional `appsettings.Local.json` next to the application, `%ProgramData%\PapercutPay\appsettings.Production.json`, environment variables (`Worldline__ApiSecret`) and the command line. Keep secrets in the ProgramData file or in environment variables, never in the repository. A complete template is in [`src/PapercutPay/appsettings.Production.example.json`](src/PapercutPay/appsettings.Production.example.json).

### `App`

| Key | Default | Meaning |
| --- | --- | --- |
| `Mode` | `Live` | `Live`, `Hybrid` (real Worldline, in-memory PaperCut) or `Fake` (everything simulated). Hybrid and Fake need `AllowFakeMode` outside Development |
| `PublicBaseUrl` | required | Public URL of the service including any virtual path, used for the Worldline return URL |
| `Currency` | `EUR` | ISO currency code |
| `Amounts` | `[5, 10, 20]` | Amounts offered, in whole currency units |
| `DbPath` | `payments.db` | SQLite ledger |
| `LinkSecret` | empty | HMAC secret for signed links; required if `AllowUnsignedLinks` is false |
| `AllowUnsignedLinks` | `true` | Accept `?user=<name>` links (see below) |
| `PaperCutUserUrl` | empty | Target of the "Continue" button |
| `Language` | `en` | `en` or `fr` for this service's pages |
| `Locale` | `en_GB` | Locale of Worldline's hosted page |
| `BrandName`, `BrandColor` | `PaperCut Pay`, `#2b5c9e` | Header name and `#rrggbb` accent color |
| `RequireHttps` | `true` | In Live mode, refuse plain HTTP except `/health` |
| `RateLimitPerMinute` | `60` | Requests per client address and minute |
| `PathBase` | empty | Only behind a proxy that does not strip a virtual path; leave empty under IIS |
| `ReconcileIntervalSeconds`, `ReconcileMinAgeSeconds`, `ReconcileMaxAgeHours` | `60`, `120`, `72` | Background check |
| `StuckCreditMinutes` | `10` | An order stuck in `CREDITING` longer than this is flagged for review |
| `AllowFakeMode` | `false` | Allow Fake/Hybrid outside Development (test machines only) |

### `PaperCut`

| Key | Meaning |
| --- | --- |
| `BaseUrl` | For example `http://papercut.example.org:9191` (HTTP) or `https://...:9192` (HTTPS) |
| `AuthToken` | Value of the `auth.webservices.auth-token` setting |
| `AccountName` | Optional: account to credit when multiple personal accounts are enabled (untested) |

### `Worldline`

| Key | Meaning |
| --- | --- |
| `BaseUrl` | Pre-production: `https://payment.preprod.direct.worldline-solutions.com`. Production: `https://payment.direct.worldline-solutions.com` |
| `MerchantId`, `ApiKeyId`, `ApiSecret` | From the Merchant Portal |
| `ApiVersion` | `v2` |
| `WebhookKeyId`, `WebhookSecret` | Only if you enable the optional webhook |
| `PaidStatuses`, `FailedStatuses` | Override which payment statuses credit or fail an order (defaults: `CAPTURED`/`PAID`) |

## Setting up PaperCut

1. In the Config Editor, set `auth.webservices.auth-token` to a long random value and `auth.webservices.allowed-addresses` to the addresses of the machine running PapercutPay. Do not leave it open to every address.
2. Add a custom link to the user web menu (Options, General) pointing to the service:

   ```
   https://print.example.org/PapercutPay/pay?user=%user%
   ```

   PaperCut replaces `%user%` with the logged-in user name.

## Setting up Worldline

- Create an API key and secret in the Merchant Portal. The hosted checkout is created with `authorizationMode=SALE`, because accounts can default to authorization only (`PENDING_CAPTURE`), which would never credit anyone.
- Payment statuses, test cards and test cases are in the [Worldline Direct documentation](https://docs.direct.worldline-solutions.com/). In pre-production, an amount of 13.02 EUR is documented as triggering a refusal.
- 3-D Secure is configured on the Worldline account, not in this service. Check that it is active before going to production.

## Operating

Commands, run from the application folder:

```bash
dotnet PapercutPay.dll orders 20                 # latest orders and their state (no secrets)
dotnet PapercutPay.dll make-link <user> 24       # signed link valid 24 hours
dotnet PapercutPay.dll pc-check <user>           # PaperCut: user exists? balance? (read only)
dotnet PapercutPay.dll pc-adjust <user> 0.01     # PaperCut: WRITES an adjustment, use a test account
dotnet PapercutPay.dll probe                     # creates a 1.00 EUR Worldline checkout and prints the exchange
dotnet PapercutPay.dll probe-status <checkoutId> # reads a checkout back
```

| State | Meaning |
| --- | --- |
| `CREATED` | Waiting for payment, or paid and waiting to be credited |
| `CREDITING` | Being credited right now |
| `CREDITED` | Done |
| `FAILED` | Cancelled, refused or expired |
| `REVIEW` | Paid but amount, currency or reference did not match: needs a human |
| `CREDIT_UNCERTAIN` | Paid, but the PaperCut call ended with an unknown outcome: compare the PaperCut balance with the payment before acting |

## Deployment

A Windows/IIS deployment script and guide are provided: see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Known limitations

- Unsigned links (`?user=%user%`) are not authenticated. See [SECURITY.md](SECURITY.md).
- A refund or chargeback after a credit is not reversed in PaperCut.
- The optional webhook follows Worldline's documentation but has not been exercised against a real webhook delivery; the background check is the supported recovery mechanism.
- One instance per ledger: SQLite is used as a single-node store.
- The IIS deployment script has been exercised on Windows Server with IIS only.

## Project layout

```
src/PapercutPay      the service
tests/PapercutPay.Tests
deploy/              IIS deployment script
docs/                deployment guide
```

## License

See [LICENSE](LICENSE).
