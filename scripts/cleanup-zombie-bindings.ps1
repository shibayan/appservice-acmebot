#Requires -Version 7.0
<#
.SYNOPSIS
    Removes confirmed-dead zombie hostname bindings and deletes orphaned
    Acmebot certificate resources. Destructive - defaults to prompting;
    use -WhatIf for a dry run.

.DESCRIPTION
    Two phases:

    1. BINDING REMOVAL - for each hostname in -HostNames (default: the 13
       bindings on easyID/easyid classified as dead on 2026-08-09 from the
       nightly digest + skip logs: 11 dead-DNS failures + 2 jwks-403 disabled
       tenants), the stale hostname binding is deleted from the site that
       carries it. SAFETY: a binding is only removed when the certificate
       bound to it is Acmebot-issued AND expired; anything else is skipped
       with a warning. Grace-period (jwks 402) zombies are protected
       structurally: they are simply not in the list.

    2. ORPHAN SWEEP - all Acmebot-issued Microsoft.Web/certificates in the
       subscription that no site binding references (including Buypass-era
       leftovers) are classified:
         - expired                        -> deleted
         - superseded (a bound, longer-lived certificate covers the same
           host names)                    -> deleted
         - valid and not superseded      -> reported only (possibly in-flight)
       Certificates whose only bindings are being removed in phase 1 count as
       orphaned, so -WhatIf previews the complete end state.

    LIMITATION: binding detection covers production sites only, not deployment
    slots of the bound sites (same as renewal-report.ps1).

    ARM api-version is pinned to 2022-03-01 throughout: newer versions return
    empty pages for the subscription-level certificate list.

    Requires an 'az login' with write access to the bound sites' resource
    groups and the certificates. Read-only under -WhatIf.

.EXAMPLE
    # Dry run - show everything that would happen
    pwsh -File scripts/cleanup-zombie-bindings.ps1 -WhatIf

.EXAMPLE
    # Execute with one confirmation prompt per destructive action
    pwsh -File scripts/cleanup-zombie-bindings.ps1

.EXAMPLE
    # Execute without prompts (e.g. after a reviewed -WhatIf run)
    pwsh -File scripts/cleanup-zombie-bindings.ps1 -Confirm:$false

.EXAMPLE
    # Test-app cleanup: explicit hostnames, keep the orphan sweep
    pwsh -File scripts/cleanup-zombie-bindings.ps1 -HostNames criipto6.rzsg.io,criipto-test.rzsg.io
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    # Subscription holding the certificates and bound sites. Defaults to the
    # current az context.
    [string] $SubscriptionId,

    # Hostnames whose bindings are confirmed dead and safe to remove.
    # Default: the production classification of 2026-08-09 (criipto-id-acme-bot).
    [string[]] $HostNames = @(
        # nightly 'No such host is known' / connect-timeout failures
        'signin.app.pixpay.se',
        'criipto.thethirdact.io',
        'idcheck.appboarding.capiq.io',
        'idfo-app.capiq.io',
        'auth.yoomey.dk',
        'kyc.thisisdoland.com',
        'bankid.nordvestfiber.no',
        'auth.thorlandcapitalmanagement.no',
        'auth.valida.rethink.clinic',
        'auth.transformed.rethink.clinic',
        'bankid.tilendre.no',
        # jwks probe returned 403 (tenant disabled/unknown)
        'justsign.mitid.dk',
        'criipto-verify-qa.tfagency.se'
    ),

    # Only run the orphan sweep (phase 2).
    [switch] $SkipBindings,

    # Only remove bindings (phase 1).
    [switch] $SkipOrphanSweep
)

$ErrorActionPreference = 'Stop'

# 'pwsh -File' passes "-HostNames a,b" as one literal string - split it up.
$HostNames = @($HostNames | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim() })

if (-not $SubscriptionId) {
    $SubscriptionId = az account show --query id -o tsv
    if ($LASTEXITCODE -ne 0) { throw "az account show failed - run 'az login' first." }
}

$apiVersion = '2022-03-01'
$now = (Get-Date).ToUniversalTime()

function Invoke-ArmDelete {
    param([string] $ResourceId, [string] $Description)
    az rest --method delete --url "https://management.azure.com$($ResourceId)?api-version=$apiVersion" -o none
    if ($LASTEXITCODE -ne 0) { Write-Warning "FAILED: delete $Description ($ResourceId)"; return $false }
    Write-Host "  deleted: $Description" -ForegroundColor Green
    $true
}

# --- Inventory --------------------------------------------------------------------

Write-Host "Loading certificate inventory and site bindings (subscription $SubscriptionId)..." -ForegroundColor Cyan

$certList = (az rest --method get --url "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.Web/certificates?api-version=$apiVersion" | ConvertFrom-Json).value
$acmebotCerts = @($certList | Where-Object { $_.tags.Issuer -eq 'Acmebot' })

$apps = az webapp list --query "[].{name:name, rg:resourceGroup, id:id, states:hostNameSslStates}" -o json | ConvertFrom-Json

# thumbprint -> list of @{ Site; SiteId; HostName }
$boundRefs = @{}
foreach ($app in $apps) {
    foreach ($state in $app.states) {
        if (-not $state.thumbprint) { continue }
        if (-not $boundRefs.ContainsKey($state.thumbprint)) { $boundRefs[$state.thumbprint] = [System.Collections.Generic.List[object]]::new() }
        $boundRefs[$state.thumbprint].Add([pscustomobject]@{ Site = "$($app.rg)/$($app.name)"; SiteId = $app.id; HostName = $state.name })
    }
}

$certByThumbprint = @{}
foreach ($c in $acmebotCerts) { $certByThumbprint[$c.properties.thumbprint] = $c }

Write-Host "  $($acmebotCerts.Count) Acmebot certificates, $($apps.Count) sites scanned."

$removedHostNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$stats = @{ bindingsRemoved = 0; certsDeleted = 0; skipped = 0; reportedOnly = 0 }

# --- Phase 1: binding removal -------------------------------------------------------

if (-not $SkipBindings) {
    Write-Host ''
    Write-Host "=== Phase 1: removing $($HostNames.Count) dead hostname binding(s) ===" -ForegroundColor Cyan

    foreach ($hostName in $HostNames) {
        $refs = @($apps | ForEach-Object {
                $app = $_
                $app.states | Where-Object { $_.name -eq $hostName -and $_.thumbprint } |
                    ForEach-Object { [pscustomobject]@{ App = $app; Thumbprint = $_.thumbprint } }
            })

        if (-not $refs) {
            Write-Warning "$hostName`: no bound site found (already cleaned up, or not visible to this account) - skipped."
            $stats.skipped++
            continue
        }

        foreach ($ref in $refs) {
            $site = "$($ref.App.rg)/$($ref.App.name)"
            $cert = $certByThumbprint[$ref.Thumbprint]

            # SAFETY: only ever remove bindings backed by an expired Acmebot certificate.
            if (-not $cert) {
                Write-Warning "$hostName on $site`: bound certificate $($ref.Thumbprint) is not Acmebot-issued - skipped."
                $stats.skipped++
                continue
            }

            $expires = ([datetime]$cert.properties.expirationDate).ToUniversalTime()

            if ($expires -ge $now) {
                Write-Warning "$hostName on $site`: bound certificate does not expire until $expires - skipped (not a zombie?)."
                $stats.skipped++
                continue
            }

            if ($PSCmdlet.ShouldProcess("$hostName on $site (cert expired $($expires.ToString('yyyy-MM-dd')))", 'Delete hostname binding')) {
                if (Invoke-ArmDelete "$($ref.App.id)/hostNameBindings/$hostName" "binding $hostName on $site") {
                    $stats.bindingsRemoved++
                }
            }

            # Feed the orphan sweep either way: with -WhatIf nothing was deleted, but
            # the sweep must preview the post-removal state.
            $null = $removedHostNames.Add($hostName)
        }
    }
}

# --- Phase 2: orphaned certificate resources ---------------------------------------

if (-not $SkipOrphanSweep) {
    Write-Host ''
    Write-Host '=== Phase 2: orphaned Acmebot certificate resources ===' -ForegroundColor Cyan

    foreach ($cert in ($acmebotCerts | Sort-Object { $_.properties.expirationDate })) {
        $thumbprint = $cert.properties.thumbprint
        $expires = ([datetime]$cert.properties.expirationDate).ToUniversalTime()
        $names = @($cert.properties.hostNames)
        $label = "$($names -join ',') (expires $($expires.ToString('yyyy-MM-dd')), $thumbprint)"

        # Bindings that will still exist after phase 1.
        $effectiveRefs = @()
        if ($boundRefs.ContainsKey($thumbprint)) {
            $effectiveRefs = @($boundRefs[$thumbprint] | Where-Object { -not $removedHostNames.Contains($_.HostName) })
        }

        if ($effectiveRefs.Count -gt 0) { continue }   # still bound somewhere - not an orphan

        if ($expires -lt $now) {
            if ($PSCmdlet.ShouldProcess($label, 'Delete orphaned EXPIRED certificate resource')) {
                if (Invoke-ArmDelete $cert.id "expired orphan $($names -join ',')") { $stats.certsDeleted++ }
            }
            continue
        }

        # Not expired: superseded when some BOUND Acmebot certificate with a later
        # expiry covers all of this certificate's host names.
        $superseded = $false
        foreach ($candidate in $acmebotCerts) {
            if ($candidate.properties.thumbprint -eq $thumbprint) { continue }
            if (([datetime]$candidate.properties.expirationDate).ToUniversalTime() -le $expires) { continue }
            if (-not $boundRefs.ContainsKey($candidate.properties.thumbprint)) { continue }
            if (@($boundRefs[$candidate.properties.thumbprint] | Where-Object { -not $removedHostNames.Contains($_.HostName) }).Count -eq 0) { continue }

            $covered = $true
            foreach ($n in $names) {
                if ($n -notin @($candidate.properties.hostNames)) { $covered = $false; break }
            }
            if ($covered) { $superseded = $true; break }
        }

        if ($superseded) {
            if ($PSCmdlet.ShouldProcess($label, 'Delete orphaned SUPERSEDED certificate resource')) {
                if (Invoke-ArmDelete $cert.id "superseded orphan $($names -join ',')") { $stats.certsDeleted++ }
            }
        }
        else {
            Write-Host "  report-only (valid, unbound, not superseded - possibly in-flight): $label" -ForegroundColor Yellow
            $stats.reportedOnly++
        }
    }
}

# --- Summary ------------------------------------------------------------------------

Write-Host ''
Write-Host ("Summary: {0} binding(s) removed, {1} certificate resource(s) deleted, {2} skipped by safety checks, {3} valid orphan(s) reported only." -f
    $stats.bindingsRemoved, $stats.certsDeleted, $stats.skipped, $stats.reportedOnly) -ForegroundColor Cyan
if ($WhatIfPreference) {
    Write-Host '(-WhatIf run: nothing was changed.)' -ForegroundColor DarkGray
}
