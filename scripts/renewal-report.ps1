#Requires -Version 7.0
<#
.SYNOPSIS
    Renders a per-instance report of Acmebot certificate renewal runs from
    Application Insights telemetry (Phase A - no app changes required).

.DESCRIPTION
    Queries the shared App Insights component for the traces and outbound ARM
    calls the renewal orchestration already emits, and prints a digest per
    Acmebot instance (production app and each slot, keyed by cloud_RoleName):

      - renewal orchestrations started
      - certificates reported as expiring
      - certificates actually renewed (detected via the ARM certificate PUT)
      - per-certificate renewal failures (thumbprints resolved to DNS names)
      - endpoint-mismatch skips
      - "Certificates are not found" occurrences

    A final cross-instance section lists "zombie bindings": expired
    Acmebot-issued certificates that are still bound to a site. These fail
    renewal every night and are hard to locate in the portal; the report names
    the site and hostname so the binding can be removed directly.

    Timestamps are printed in Danish locale (da-DK); times are UTC.

    LIMITATIONS (until the structured run-summary lands - "Phase B"): a run
    that saw an EMPTY certificate inventory is indistinguishable from a quiet
    night; both log "Certificates are not found". Zombie detection covers
    production sites only (not deployment slots of the bound sites).

.EXAMPLE
    pwsh -File scripts/renewal-report.ps1

.EXAMPLE
    # Report over the last 3 days
    pwsh -File scripts/renewal-report.ps1 -WindowHours 72
#>
[CmdletBinding()]
param(
    [string] $AppInsightsComponent = 'appi-criiptoioacme-ogqh',

    [string] $ResourceGroup = 'Test-and-Development',

    # Subscription holding the certificates and bound sites. Defaults to the
    # current az context.
    [string] $SubscriptionId,

    # Look-back window. Default covers the nightly 00:00 UTC run with slack.
    [int] $WindowHours = 26
)

$ErrorActionPreference = 'Stop'
$env:AZURE_EXTENSION_USE_DYNAMIC_INSTALL = 'yes_without_prompt'

$danish = [System.Globalization.CultureInfo]::GetCultureInfo('da-DK')

function Format-Ts {
    param([datetime] $Value)
    # Normalize to UTC so all report timestamps agree (JSON parsing yields local-kind dates).
    if ($Value.Kind -eq [System.DateTimeKind]::Local) { $Value = $Value.ToUniversalTime() }
    $Value.ToString('g', $danish)
}

function Invoke-AiQuery {
    param([string] $Query)

    $json = az monitor app-insights query --app $AppInsightsComponent -g $ResourceGroup --analytics-query $Query -o json
    if ($LASTEXITCODE -ne 0) { throw "App Insights query failed." }

    $table = ($json | ConvertFrom-Json).tables[0]
    foreach ($row in $table.rows) {
        $obj = [ordered]@{}
        for ($i = 0; $i -lt $table.columns.Count; $i++) { $obj[$table.columns[$i].name] = $row[$i] }
        [pscustomobject]$obj
    }
}

if (-not $SubscriptionId) {
    $SubscriptionId = az account show --query id -o tsv
}

$win = "${WindowHours}h"

# --- App Insights: what the renewal runs did -------------------------------------

# Renewal orchestrations started (timer- or manually-triggered)
$runs = Invoke-AiQuery "traces | where timestamp > ago($win) | where operation_Name == 'RenewCertificates_Timer' and message startswith 'Started orchestration' | project timestamp, cloud_RoleName"

# Certificates the orchestrator considered expiring
$expiring = Invoke-AiQuery "traces | where timestamp > ago($win) | where message startswith 'Expiring certificate :' | project timestamp, cloud_RoleName, message"

# Successful renewals: the certificate upload is a PUT on Microsoft.Web/certificates/{dns}-{thumbprint}
$renewed = Invoke-AiQuery "dependencies | where timestamp > ago($win) | where name startswith 'PUT ' and name contains '/providers/Microsoft.Web/certificates/' and resultCode == 200 | extend certName = tostring(split(name, '/')[-1]) | project timestamp, cloud_RoleName, certName"

# Per-certificate failures (guarded by the per-cert try/catch in the sub-orchestrator)
$failed = Invoke-AiQuery "traces | where timestamp > ago($win) | where message startswith 'Failed renewal in sub orchestration' or message startswith 'Failed sub orchestration' | project timestamp, cloud_RoleName, message"

# Endpoint-mismatch skips (certificates this instance will never renew)
$skipped = Invoke-AiQuery "traces | where timestamp > ago($win) | where message startswith 'Skipping Acmebot-issued certificate' | summarize n = count() by cloud_RoleName, message"

# Empty-result runs
$notFound = Invoke-AiQuery "traces | where timestamp > ago($win) | where message == 'Certificates are not found' | summarize n = count() by cloud_RoleName"

# --- ARM: certificate inventory and bindings (zombies, thumbprint resolution) ----

# NOTE: api-version is pinned to 2022-03-01 on purpose; newer api-versions return
# only empty pages for the subscription-level list (same issue as Startup.cs).
$certList = (az rest --method get --url "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.Web/certificates?api-version=2022-03-01" | ConvertFrom-Json).value

$acmebotCerts = @($certList | Where-Object { $_.tags.Issuer -eq 'Acmebot' })
$certByThumbprint = @{}
foreach ($c in $acmebotCerts) { $certByThumbprint[$c.properties.thumbprint] = $c }

$apps = az webapp list --query "[].{name:name, rg:resourceGroup, states:hostNameSslStates}" -o json | ConvertFrom-Json

$zombies = foreach ($app in $apps) {
    foreach ($state in $app.states) {
        if (-not $state.thumbprint -or -not $certByThumbprint.ContainsKey($state.thumbprint)) { continue }

        $cert = $certByThumbprint[$state.thumbprint]
        $expires = [datetime]$cert.properties.expirationDate

        if ($expires -lt (Get-Date)) {
            [pscustomobject]@{
                Site       = "$($app.rg)/$($app.name)"
                HostName   = $state.name
                Expired    = $expires
                Endpoint   = $cert.tags.Endpoint
                Thumbprint = $state.thumbprint
            }
        }
    }
}

# --- Render -----------------------------------------------------------------------

function Resolve-DnsName {
    param([string] $Message)

    # Failure messages carry only the thumbprint; resolve to DNS names when possible.
    if ($Message -match '([0-9A-F]{40})' -and $certByThumbprint.ContainsKey($Matches[1])) {
        $hostNames = $certByThumbprint[$Matches[1]].properties.hostNames -join ','
        return "$Message  [$hostNames]"
    }
    return $Message
}

$roles = @($runs + $expiring + $renewed + $failed + $skipped + $notFound | ForEach-Object { $_.cloud_RoleName } | Sort-Object -Unique)

Write-Host "Acmebot renewal report - last $WindowHours h (generated $(Format-Ts (Get-Date -AsUTC)) UTC)" -ForegroundColor Cyan

if (-not $roles) {
    Write-Warning 'No renewal-related telemetry found in the window. Verify the runs are firing at all.'
}

foreach ($role in $roles) {
    Write-Host ''
    Write-Host "=== $role ===" -ForegroundColor Cyan

    $r = @($runs | Where-Object cloud_RoleName -eq $role)
    # Durable orchestrators re-log on every replay, so dedupe orchestrator-emitted lines.
    $e = @($expiring | Where-Object cloud_RoleName -eq $role | Sort-Object message -Unique)
    $n = @($renewed | Where-Object cloud_RoleName -eq $role)
    $f = @($failed | Where-Object cloud_RoleName -eq $role | Sort-Object message -Unique)
    $s = @($skipped | Where-Object cloud_RoleName -eq $role)
    $q = @($notFound | Where-Object cloud_RoleName -eq $role)

    Write-Host "Runs started: $($r.Count)   Expiring found: $($e.Count)   Renewed: $($n.Count)   Failures: $($f.Count)   Empty runs: $(($q | Measure-Object -Sum n).Sum ?? 0)"

    if ($n) {
        Write-Host 'Renewed:' -ForegroundColor Green
        # Certificate resource names are {dnsName}-{thumbprint}; strip the trailing thumbprint.
        $n | ForEach-Object { "  {0}  {1}" -f (Format-Ts ([datetime]$_.timestamp)), ($_.certName -replace '-[0-9A-F]{40}$', '') } | Sort-Object -Unique
    }

    if ($f) {
        Write-Host 'Failures:' -ForegroundColor Red
        $f | ForEach-Object { "  {0}  {1}" -f (Format-Ts ([datetime]$_.timestamp)), (Resolve-DnsName $_.message) }
    }

    if ($e) {
        Write-Host 'Expiring certificates seen:'
        $e | ForEach-Object {
            # Message format: "Expiring certificate : {subject} - {expiry:invariant}"
            $body = $_.message -replace '^Expiring certificate : ', ''
            if ($body -match '^(.+) - (.+)$') {
                $expiryText = try { Format-Ts ([datetime]::Parse($Matches[2], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal)) } catch { $Matches[2] }
                "  $($Matches[1]) - $expiryText"
            } else { "  $body" }
        }
    }

    if ($s) {
        Write-Host "Endpoint-mismatch skips (never renewed by this instance): $(($s | Measure-Object -Sum n).Sum)"
    }

    if ($r.Count -gt 0 -and $e.Count -eq 0 -and $n.Count -eq 0 -and $f.Count -eq 0) {
        Write-Warning 'Run(s) completed without seeing any expiring certificates. This is EITHER a quiet night OR an empty certificate inventory (see LIMITATIONS in the header).'
    }
}

Write-Host ''
Write-Host "=== Zombie bindings (expired Acmebot certificates still bound to a site) ===" -ForegroundColor Cyan

if ($zombies) {
    Write-Host 'These fail renewal every night. Remove the stale hostname binding (or rebind to a valid certificate) on the listed site to silence them.'
    $zombies | Sort-Object Expired | ForEach-Object {
        "  {0}  ->  {1}  (expired {2}, {3}, thumbprint {4})" -f $_.Site, $_.HostName, (Format-Ts $_.Expired), $_.Endpoint, $_.Thumbprint
    }
}
else {
    Write-Host 'None found.'
}
