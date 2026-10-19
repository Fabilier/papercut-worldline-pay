# Deploying on Windows / IIS

> English | [Français](DEPLOYMENT.fr.md)

PapercutPay runs as its **own IIS application** (for example `/PapercutPay`) with its own application pool. The deployment script never touches other applications, so a previous payment setup stays in place as your rollback.

## Before you start

1. Install the **ASP.NET Core 8 Hosting Bundle** on the server. The bundle, not only the runtime: it provides the IIS module. Then run `iisreset` (this interrupts IIS for a few seconds, so choose a quiet moment).
2. Use **Windows PowerShell 5.1** (`powershell.exe`) as Administrator. The IIS module does not load in PowerShell 7.
3. Note the **IIS site name** that should host the application.
4. Make sure the server can reach Worldline over HTTPS and PaperCut's Web Services port, and that its address is in PaperCut's `auth.webservices.allowed-addresses`.
5. Optional but recommended: back up the IIS configuration so it can be restored in one command:

   ```powershell
   & "$env:windir\system32\inetsrv\appcmd.exe" add backup "before-papercutpay"
   ```

## Build the package

```powershell
dotnet publish src/PapercutPay --configuration Release --output deploy/package
```

Copy the `deploy` folder (the script and `package`) to the server, for example into `C:\Temp\PapercutPay-deploy`. If Windows marked the files as downloaded, unblock them (right-click, Properties, Unblock) or run `Get-ChildItem -Recurse | Unblock-File`.

## Install

```powershell
cd C:\Temp\PapercutPay-deploy
.\Deploy-PapercutPay.ps1 -SiteName "Default Web Site" -DryRun   # prints every action, changes nothing
.\Deploy-PapercutPay.ps1 -SiteName "Default Web Site"
```

Optionally pass `-LegacyAppName <name>` to make the script refuse to touch an existing application, pool or folder of that name.

The script:

- creates the pool `PapercutPay` (No Managed Code, always running, no idle shutdown, which the background check needs);
- copies the application to `C:\inetpub\PapercutPay`;
- creates `C:\ProgramData\PapercutPay\` for the settings file, the `payments.db` ledger and backups, outside the application folder so redeploying never touches them, and restricts it to Administrators, SYSTEM and the pool identity (well-known SIDs, so it works on any Windows language);
- backs up the previous version before replacing it;
- is safe to run again: it keeps the settings file and the ledger.

## Settings

Fill in `C:\ProgramData\PapercutPay\appsettings.Production.json` (the script creates a template). See the [configuration reference](../README.md#configuration). At least:

- `App:PublicBaseUrl`, `App:LinkSecret`
- `PaperCut:BaseUrl`, `PaperCut:AuthToken`
- `Worldline:MerchantId`, `ApiKeyId`, `ApiSecret` (keep `BaseUrl` on pre-production until you have tested)

Then recycle the pool and check the service:

```powershell
Restart-WebAppPool PapercutPay
curl.exe -s http://localhost:<port>/PapercutPay/health
```

Expected: `{"status":"ok","mode":"Live"}`. If the service does not start, read the Application event log: it lists the missing settings. Plain HTTP is refused for everything except `/health` (`App:RequireHttps`), so test the payment form through your HTTPS address. If your HTTPS binding is tied to a host name, `https://localhost` will not match it.

## Acceptance test in pre-production

1. Generate a signed link for a test account, from `C:\inetpub\PapercutPay`:

   ```powershell
   & "$env:ProgramFiles\dotnet\dotnet.exe" .\PapercutPay.dll make-link some.user 1
   ```

   (A session opened before the Hosting Bundle was installed does not know `dotnet`: use the full path, as above.)
2. Pay the smallest amount with a Worldline test card. The balance must increase once.
3. Reload the result page: it must say the payment was already processed.
4. Cancel a payment with the button on Worldline's page: the order must end `FAILED`.
5. Pay, then close the tab before returning: the credit must appear on its own within one or two minutes (check with the `orders` command).
6. Undo the test credit with an adjustment in PaperCut.

## Going to production

1. In the settings file, set `Worldline:BaseUrl` to `https://payment.direct.worldline-solutions.com` and use the **production** API key and secret.
2. Recycle the pool and run one real low-value transaction; reconcile it in the Merchant Portal and in PaperCut.
3. Switch the PaperCut custom link to the new service, for example `https://print.example.org/PapercutPay/pay?user=%user%`. The change is immediate for all users and is undone by restoring the old text: write the old value down first.
4. After a stabilisation period, rotate the PaperCut token and review `auth.webservices.allowed-addresses`.

## Updating

Rebuild the package, copy it, and run the script again. It keeps your settings and ledger, backs up the current version, replaces it, and restarts only the PapercutPay pool.

## Rolling back

```powershell
.\Deploy-PapercutPay.ps1 -SiteName "Default Web Site" -Rollback   # restores the latest code backup
Stop-WebAppPool PapercutPay                                       # take the service offline
```

The ledger stays compatible with earlier versions. To go back to a previous payment setup completely, restore the old PaperCut custom link and stop the pool.

## Manual recovery

List the latest orders (no secrets are printed):

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" C:\inetpub\PapercutPay\PapercutPay.dll orders 20
```

Orders in `REVIEW` or `CREDIT_UNCERTAIN` are never replayed automatically: compare the Merchant Portal and the PaperCut balance before acting. `CREDIT_UNCERTAIN` is also set for an order that stayed in `CREDITING` longer than `App:StuckCreditMinutes` (for example the server stopped between the PaperCut call and the database update).
