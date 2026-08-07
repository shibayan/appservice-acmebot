#Requires -Version 7.0
<#
.SYNOPSIS
    Presents an overview of upcoming certificate renewals that are in scope for
    the Acmebot instances, grouped by ACME endpoint.

.DESCRIPTION
    "In scope" means: the certificate is Acmebot-issued (Issuer tag) AND its
    thumbprint is currently bound to a site, which is what the nightly renewal
    orchestration acts on. For each such certificate the expected renewal date is
    computed as (expiry - RenewBeforeExpiryDays); certificates already inside the
    window (or expired - the "zombies") renew/are attempted on the next nightly run.

    The report focuses on renewals starting within the next -HorizonDays. If no
    certificate falls inside the horizon, the single nearest upcoming renewal is
    shown instead, so the report always contains at least one entry.

    Endpoint groups are labeled with the owning Acmebot instance when it can be
    resolved from the function app's (and its slots') Acmebot:Endpoint settings.

    Requires an 'az login' account with read access to the function-app
    environment (certificates, web apps, and function app settings).

    LIMITATION: binding detection covers production sites only, not deployment
    slots of the bound sites (same as scripts/renewal-report.ps1).

.EXAMPLE
    pwsh -File scripts/upcoming-renewals.ps1

.EXAMPLE
    # Look 8 weeks ahead instead
    pwsh -File scripts/upcoming-renewals.ps1 -HorizonDays 56
#>
[CmdletBinding()]
param(
    # Subscription holding the certificates and bound sites. Defaults to the
    # current az context.
    [string] $SubscriptionId,

    # Function app whose (slot) settings map ACME endpoints to instances.
    [string] $FunctionAppName = 'criipto-io-acme-bot',

    [string] $ResourceGroup = 'Test-and-Development',

    # How far ahead renewal start dates are considered "near-future".
    [int] $HorizonDays = 28,

    # Must match the Acmebot:RenewBeforeExpiry setting of the instances (default 30).
    [int] $RenewBeforeExpiryDays = 30
)

$ErrorActionPreference = 'Stop'

$danish = [System.Globalization.CultureInfo]::GetCultureInfo('da-DK')

function Format-Day {
    param([datetime] $Value)
    if ($Value.Kind -eq [System.DateTimeKind]::Local) { $Value = $Value.ToUniversalTime() }
    $Value.ToString('d', $danish)
}

# Mirrors CertificateExtensions.NormalizeEndpoint: legacy tags hold the full URL,
# newer ones only the host.
function Get-EndpointHost {
    param([string] $Endpoint)
    $uri = $null
    if ([Uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref]$uri)) { return $uri.Host }
    return $Endpoint
}

if (-not $SubscriptionId) {
    $SubscriptionId = az account show --query id -o tsv
    if ($LASTEXITCODE -ne 0) { throw "az account show failed - run 'az login' first." }
}

$now = (Get-Date).ToUniversalTime()

# --- Certificates (api-version pinned: newer versions return empty pages at
# --- subscription scope, same issue documented in Program.cs) ------------------
$certs = (az rest --method get --url "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.Web/certificates?api-version=2022-03-01" | ConvertFrom-Json).value |
    Where-Object { $_.tags.Issuer -eq 'Acmebot' }

$certByThumbprint = @{}
foreach ($c in $certs) { $certByThumbprint[$c.properties.thumbprint] = $c }

# --- Bound thumbprints (production sites) --------------------------------------
$apps = az webapp list --query "[?state=='Running'].{name:name, rg:resourceGroup, states:hostNameSslStates}" -o json | ConvertFrom-Json

$bound = @{}
foreach ($app in $apps) {
    foreach ($state in $app.states) {
        if ($state.thumbprint -and $certByThumbprint.ContainsKey($state.thumbprint) -and -not $bound.ContainsKey($state.thumbprint)) {
            $bound[$state.thumbprint] = "$($app.rg)/$($app.name)"
        }
    }
}

# --- Map endpoint hosts to owning Acmebot instances (best effort) --------------
$instanceByEndpoint = @{}
try {
    $slotNames = @(az functionapp deployment slot list -g $ResourceGroup -n $FunctionAppName --query '[].name' -o tsv 2>$null)
    $targets = @(@{ label = "$FunctionAppName (production)"; slotArgs = @() })
    foreach ($s in $slotNames) { $targets += @{ label = "$FunctionAppName/$s"; slotArgs = @('--slot', $s) } }

    foreach ($t in $targets) {
        $ep = az functionapp config appsettings list -g $ResourceGroup -n $FunctionAppName @($t.slotArgs) --query "[?name=='Acmebot:Endpoint'].value | [0]" -o tsv 2>$null
        if ($ep) { $instanceByEndpoint[(Get-EndpointHost $ep)] = $t.label }
    }
}
catch {
    Write-Verbose "Instance mapping skipped: $($_.Exception.Message)"
}

# --- Build the schedule ---------------------------------------------------------
$entries = foreach ($thumbprint in $bound.Keys) {
    $cert = $certByThumbprint[$thumbprint]
    $expires = ([datetime]$cert.properties.expirationDate).ToUniversalTime()
    $renewalStart = $expires.AddDays(-$RenewBeforeExpiryDays)

    [pscustomobject]@{
        Endpoint     = Get-EndpointHost $cert.tags.Endpoint
        DnsNames     = $cert.properties.hostNames -join ','
        Site         = $bound[$thumbprint]
        Expires      = $expires
        RenewalStart = $renewalStart
        Overdue      = $expires -lt $now
    }
}

$horizonEnd = $now.AddDays($HorizonDays)
$inHorizon = @($entries | Where-Object { $_.RenewalStart -le $horizonEnd })

$fallbackUsed = $false
if (-not $inHorizon) {
    # Never produce an empty report: fall back to the single nearest upcoming renewal.
    $inHorizon = @($entries | Sort-Object RenewalStart | Select-Object -First 1)
    $fallbackUsed = $true
}

# --- Render ---------------------------------------------------------------------
Write-Host "Upcoming Acmebot renewals - next $HorizonDays days (generated $(Format-Day $now), renewal window $RenewBeforeExpiryDays days before expiry)" -ForegroundColor Cyan

if (-not $inHorizon) {
    Write-Warning 'No bound Acmebot-issued certificates found at all.'
    return
}

if ($fallbackUsed) {
    Write-Warning "Nothing is due within $HorizonDays days; showing the nearest upcoming renewal instead."
}

foreach ($group in ($inHorizon | Group-Object Endpoint | Sort-Object Name)) {
    $label = if ($instanceByEndpoint.ContainsKey($group.Name)) { "$($group.Name)  [$($instanceByEndpoint[$group.Name])]" } else { $group.Name }

    Write-Host ''
    Write-Host "=== $label ===" -ForegroundColor Cyan

    foreach ($e in ($group.Group | Sort-Object RenewalStart)) {
        if ($e.Overdue) {
            Write-Host ("  OVERDUE (expired {0}, attempted nightly - likely zombie)  {1}  on {2}" -f (Format-Day $e.Expires), $e.DnsNames, $e.Site) -ForegroundColor Red
        }
        elseif ($e.RenewalStart -le $now) {
            Write-Host ("  next nightly run (in window since {0}, expires {1})  {2}  on {3}" -f (Format-Day $e.RenewalStart), (Format-Day $e.Expires), $e.DnsNames, $e.Site) -ForegroundColor Yellow
        }
        else {
            Write-Host ("  renews {0} (expires {1})  {2}  on {3}" -f (Format-Day $e.RenewalStart), (Format-Day $e.Expires), $e.DnsNames, $e.Site)
        }
    }
}

$beyond = @($entries | Where-Object { $_.RenewalStart -gt $horizonEnd }).Count
if ($beyond -gt 0) {
    Write-Host ''
    Write-Host "($beyond further bound certificate(s) renew beyond the horizon.)"
}
