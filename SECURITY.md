# Security

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use the repository's **private vulnerability reporting** (Security tab, "Report a vulnerability") or contact the maintainers privately. Include steps to reproduce and the affected version.

## Security model

| Concern | What the service does |
| --- | --- |
| Card data | Never handled. The customer pays on Worldline's hosted page |
| Deciding to credit | Only from the server-to-server Worldline response, compared with the stored order (amount, currency, reference). Return URL parameters and webhook bodies are never trusted |
| Double credit | Atomic `CREATED` to `CREDITING` claim in the database; browser return, background check and webhook cannot both win |
| Unknown outcomes | A PaperCut timeout flags the order for human review instead of replaying it |
| Secrets | Outside the application folder or in environment variables; the published package contains no settings files with secrets |
| Transport | HTTPS required in Live mode (except `/health`) |
| Abuse | Per-address rate limiting, request body limits on unauthenticated endpoints |
| Pages | Strict Content-Security-Policy, no framing, no caching, no referrer, no script, output HTML-encoded |
| Test endpoints | `Fake` and `Hybrid` modes (which expose `/dev/*`) are refused outside Development unless explicitly allowed |
| PaperCut answers | XML parsed with DTD processing prohibited |

## Known limitations you must accept or mitigate

1. **Unsigned links.** The PaperCut custom link `?user=%user%` carries a plain user name. Anyone who can guess a user name can open the payment form for that account and pay for it (the balance is not shown, and nothing can be taken from the account). User names can be enumerated by trying links. Mitigations: keep the service behind your normal network controls, and prefer signed links (`make-link`, parameter `t=`) with `App:AllowUnsignedLinks=false` if you can generate them from an authenticated portal.
2. **PaperCut Web Services token.** Whoever has the token and an allowed address can adjust any balance. Use a long random token, restrict `auth.webservices.allowed-addresses` to the machines that need it, and prefer HTTPS (port 9192) or a loopback address when the service runs on the PaperCut server.
3. **Tokens in URLs.** Signed links appear in web server logs. Keep their lifetime short.
4. **Refunds and chargebacks** are not mirrored in PaperCut.
5. **No HSTS.** The service does not send `Strict-Transport-Security`, because HSTS applies to the whole host name and could break other services on it. Add it at the web server if appropriate.
6. **3-D Secure** depends on your Worldline account configuration.

## Hardening checklist before production

- [ ] Worldline production keys in a protected settings file, `Worldline:BaseUrl` set to production
- [ ] Long random `auth.webservices.auth-token`; allowed addresses restricted
- [ ] `App:LinkSecret` set; decision taken on `App:AllowUnsignedLinks`
- [ ] HTTPS binding in place; plain HTTP closed or restricted
- [ ] 3-D Secure confirmed active on the Worldline account
- [ ] One real low-value transaction reconciled between the Merchant Portal and PaperCut
- [ ] Backups of `payments.db` and a procedure for orders in `REVIEW` or `CREDIT_UNCERTAIN`
