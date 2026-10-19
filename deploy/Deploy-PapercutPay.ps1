<#
.SYNOPSIS
    Deploys PapercutPay as its own IIS application, next to any existing application on the same site.

.DESCRIPTION
    - Creates (or updates) an application <SiteName>/<AppName> with a dedicated application pool (No Managed Code).
    - Keeps secrets and the payments database OUTSIDE the application folder (DataPath), so redeploying never touches them.
    - Never modifies other applications or pools. Pass -LegacyAppName to make the script refuse to touch a specific one.
    - Backs up the previous version before replacing it; -Rollback restores the latest backup.

    Run it from an elevated PowerShell on the IIS server. Use -DryRun first: it prints every action and changes nothing.

.EXAMPLE
    .\Deploy-PapercutPay.ps1 -SiteName "Default Web Site" -DryRun
    .\Deploy-PapercutPay.ps1 -SiteName "Default Web Site"
    .\Deploy-PapercutPay.ps1 -SiteName "Default Web Site" -Rollback
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SiteName,
    [string] $AppName = 'PapercutPay',
    [string] $PoolName = 'PapercutPay',
    [string] $PackagePath = '',
    [string] $PhysicalPath = 'C:\inetpub\PapercutPay',
    [string] $DataPath = '',
    [string] $LegacyAppName = '',
    [switch] $DryRun,
    [switch] $Rollback
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $PackagePath) { $PackagePath = Join-Path $scriptDir 'package' }
if (-not $DataPath) { $DataPath = Join-Path $env:ProgramData 'PapercutPay' }

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function Invoke-Step([string] $Description, [scriptblock] $Action) {
    if ($DryRun) {
        Write-Host "[dry-run] $Description" -ForegroundColor Yellow
    }
    else {
        Write-Step $Description
        & $Action
    }
}

function Stop-OrWarn([string] $Message) {
    if ($DryRun) { Write-Warning "[dry-run] a real run would stop here: $Message" } else { throw $Message }
}

function Test-IisPath([string] $Path) {
    try { return (Test-Path $Path) } catch { return $false }
}

function Assert-Admin {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated (Administrator) PowerShell.'
    }
}

function Grant-Access([string] $Path, [string] $Identity, [string] $Rights) {
    $acl = Get-Acl $Path
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($Identity, $Rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -Path $Path -AclObject $acl
}

$poolIdentity = "IIS AppPool\$PoolName"
$backupRoot = Join-Path $DataPath 'backups'
$appPath = "IIS:\Sites\$SiteName\$AppName"

if (-not $DryRun) { Assert-Admin }

# --- Prerequisites -------------------------------------------------------------------------------------------
Write-Step 'Checking prerequisites'
if ($PSVersionTable.PSVersion.Major -ge 6) {
    Stop-OrWarn 'This script needs Windows PowerShell 5.1 (powershell.exe, the blue icon): the IIS module does not load in PowerShell 7. Reopen it as Administrator.'
}
if (Get-Module -ListAvailable -Name WebAdministration) {
    Import-Module WebAdministration
}
else {
    Stop-OrWarn 'The IIS PowerShell module (WebAdministration) is missing. Install the IIS management scripts and tools.'
}

$moduleDll = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
if (-not (Test-Path $moduleDll)) {
    Stop-OrWarn "ASP.NET Core Module V2 not found. Install the 'ASP.NET Core 8 Hosting Bundle' (Hosting Bundle, not just the runtime), then run 'iisreset'."
}
$runtimeDir = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.AspNetCore.App'
if (-not (Test-Path $runtimeDir) -or -not (Get-ChildItem $runtimeDir -Directory | Where-Object Name -like '8.*')) {
    Stop-OrWarn 'ASP.NET Core 8 runtime not found. Install the ASP.NET Core 8 Hosting Bundle.'
}
if (-not (Test-IisPath "IIS:\Sites\$SiteName")) {
    Stop-OrWarn "IIS site '$SiteName' not found."
}
if ($LegacyAppName -and ($AppName -ieq $LegacyAppName -or $PoolName -ieq $LegacyAppName -or (Split-Path $PhysicalPath -Leaf) -ieq $LegacyAppName)) {
    throw "Refusing to touch the legacy application '$LegacyAppName', its pool or its folder."
}

# --- Rollback ------------------------------------------------------------------------------------------------
if ($Rollback) {
    $latest = Get-ChildItem $backupRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
    if (-not $latest) { throw "No backup found in $backupRoot." }
    Invoke-Step "Stop pool $PoolName" { if ((Get-WebAppPoolState $PoolName).Value -ne 'Stopped') { Stop-WebAppPool $PoolName } }
    Invoke-Step "Restore $($latest.FullName) to $PhysicalPath" {
        robocopy $latest.FullName $PhysicalPath /MIR /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    }
    Invoke-Step "Start pool $PoolName" { Start-WebAppPool $PoolName }
    Write-Host "Rolled back to $($latest.Name)." -ForegroundColor Green
    return
}

# --- Package check -------------------------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $PackagePath 'PapercutPay.dll'))) {
    throw "Package not found at $PackagePath (PapercutPay.dll missing)."
}
if (Get-ChildItem $PackagePath -Recurse -Include 'appsettings.Local.json', 'appsettings.Production.json') {
    throw 'The package contains a local or production settings file. Rebuild it: secrets must never be shipped.'
}

# --- Data folder, secrets template, permissions --------------------------------------------------------------
Invoke-Step "Create data folder $DataPath" { New-Item -ItemType Directory -Force -Path $DataPath, $backupRoot | Out-Null }

$secretsFile = Join-Path $DataPath 'appsettings.Production.json'
if (Test-Path $secretsFile) {
    Write-Step "Keeping existing settings file $secretsFile"
}
else {
    Invoke-Step "Create settings template $secretsFile (fill it in before starting)" {
        $template = @{
            App       = @{
                Mode                  = 'Live'
                PublicBaseUrl         = "https://YOUR-HOST/$AppName"
                DbPath                = (Join-Path $DataPath 'payments.db')
                LinkSecret            = ''
                AllowUnsignedLinks     = $true
                PaperCutUserUrl       = ''
                Language              = 'en'
                BrandName             = 'PaperCut Pay'
                BrandColor            = '#2b5c9e'
            }
            PaperCut  = @{ BaseUrl = 'http://YOUR-PAPERCUT-SERVER:9191'; AuthToken = '' }
            Worldline = @{
                BaseUrl    = 'https://payment.preprod.direct.worldline-solutions.com'
                MerchantId = ''
                ApiKeyId   = ''
                ApiSecret  = ''
            }
        }
        $template | ConvertTo-Json -Depth 5 | Set-Content -Path $secretsFile -Encoding UTF8
    }
}

# --- Back up the current version -----------------------------------------------------------------------------
if (Test-Path $PhysicalPath) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Invoke-Step "Back up $PhysicalPath to $backupRoot\$stamp" {
        robocopy $PhysicalPath (Join-Path $backupRoot $stamp) /MIR /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    }
}

# --- Application pool ----------------------------------------------------------------------------------------
if (Test-IisPath "IIS:\AppPools\$PoolName") {
    Invoke-Step "Stop pool $PoolName" { if ((Get-WebAppPoolState $PoolName).Value -ne 'Stopped') { Stop-WebAppPool $PoolName } }
}
else {
    Invoke-Step "Create pool $PoolName (No Managed Code)" { New-WebAppPool -Name $PoolName | Out-Null }
}
Invoke-Step "Configure pool $PoolName (always running, no idle shutdown)" {
    Set-ItemProperty "IIS:\AppPools\$PoolName" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty "IIS:\AppPools\$PoolName" -Name startMode -Value 'AlwaysRunning'
    Set-ItemProperty "IIS:\AppPools\$PoolName" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    Set-ItemProperty "IIS:\AppPools\$PoolName" -Name processModel.identityType -Value 'ApplicationPoolIdentity'
}

# --- Files ---------------------------------------------------------------------------------------------------
Invoke-Step "Copy package to $PhysicalPath" {
    New-Item -ItemType Directory -Force -Path $PhysicalPath | Out-Null
    robocopy $PackagePath $PhysicalPath /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
}

# --- IIS application -----------------------------------------------------------------------------------------
if (Test-IisPath $appPath) {
    Invoke-Step "Update application $SiteName/$AppName" {
        Set-ItemProperty $appPath -Name physicalPath -Value $PhysicalPath
        Set-ItemProperty $appPath -Name applicationPool -Value $PoolName
    }
}
else {
    Invoke-Step "Create application $SiteName/$AppName" {
        New-WebApplication -Name $AppName -Site $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $PoolName | Out-Null
    }
}
Invoke-Step 'Enable preload so the background check starts without waiting for a visit' {
    Set-ItemProperty $appPath -Name preloadEnabled -Value $true
}

# --- Permissions (pool identity exists once the pool does) ---------------------------------------------------
Invoke-Step "Grant $poolIdentity read on the app folder and modify on the data folder" {
    Grant-Access $PhysicalPath $poolIdentity 'ReadAndExecute'
    Grant-Access $DataPath $poolIdentity 'Modify'
}
Invoke-Step "Restrict $DataPath (payments database, backups, settings) to Administrators, SYSTEM and the pool identity" {
    # ProgramData is readable by every local user by default; the database holds user names and amounts.
    $dirAcl = Get-Acl $DataPath
    $dirAcl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
        $id = New-Object Security.Principal.SecurityIdentifier($sid)
        $dirAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    $dirAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($poolIdentity, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -Path $DataPath -AclObject $dirAcl
}
Invoke-Step 'Restrict the settings file to Administrators, SYSTEM and the pool identity' {
    $acl = Get-Acl $secretsFile
    $acl.SetAccessRuleProtection($true, $false)
    # Well-known SIDs (Administrators, SYSTEM): account names are localized and fail to resolve on non-English Windows.
    foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
        $id = New-Object Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'Allow')))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($poolIdentity, 'Read', 'Allow')))
    Set-Acl -Path $secretsFile -AclObject $acl
}

# --- Start and smoke test ------------------------------------------------------------------------------------
Invoke-Step "Start pool $PoolName" { Start-WebAppPool $PoolName }

if (-not $DryRun) {
    $binding = Get-WebBinding -Name $SiteName | Select-Object -First 1
    $port = ($binding.bindingInformation -split ':')[1]
    $scheme = $binding.protocol
    $url = "${scheme}://localhost:$port/$AppName/health"
    Write-Step "Smoke test $url"
    Start-Sleep -Seconds 3
    $body = & curl.exe -sk --max-time 15 $url
    if ($body -match '"status":"ok"') {
        Write-Host "Service answers: $body" -ForegroundColor Green
    }
    else {
        Write-Warning "No healthy answer ($body). If the settings file is still empty this is expected: fill $secretsFile, then recycle the pool. Check the Windows Event Log (Application) for the startup error."
    }
}

Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host "  1. Fill in $secretsFile (LinkSecret, PaperCut token, Worldline keys). Only Administrators can edit it."
Write-Host "  2. Recycle the pool:  Restart-WebAppPool $PoolName"
Write-Host "  3. Check:  https://<host>:<port>/$AppName/health  (expects {""status"":""ok"",""mode"":""Live""})"
Write-Host '  4. No other application was touched; keep any previous payment setup until the new flow is validated.'
