#Requires -Version 7.0
<#
.SYNOPSIS
    One-time upgrade of an Acmebot function app (and all its deployment slots)
    from Functions runtime v3 / .NET Core 3.1 to Functions v4 / .NET 8 (in-process).

.DESCRIPTION
    App settings and site configuration are per-slot in App Service, so the same
    changes are applied to the production app and to every deployment slot, in the
    required order:

      1. FUNCTIONS_INPROC_NET8_ENABLED = 1   (must be present BEFORE the version flip)
      2. FUNCTIONS_EXTENSION_VERSION  = ~4
      3. Site config .NET version     = v8.0

    NOTE: after this script runs, the currently deployed v3 payload will no longer
    start. Run scripts/build-package.ps1 followed by scripts/publish-functionapp.ps1
    immediately afterwards to deploy the v4/.NET 8 build.

.EXAMPLE
    pwsh -File scripts/upgrade-functions-runtime.ps1 -FunctionAppName criipto-io-acme-bot-staging

.EXAMPLE
    # Upgrade a single slot only, leaving the production app untouched
    pwsh -File scripts/upgrade-functions-runtime.ps1 -FunctionAppName criipto-io-acme-bot-staging -Slots test -SkipProduction
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FunctionAppName,

    # Resolved automatically from the function app name when omitted.
    [string] $ResourceGroup,

    # Slots to configure in addition to the production app.
    # Defaults to every deployment slot of the app.
    [string[]] $Slots,

    # Skip the production app (configure slots only).
    [switch] $SkipProduction
)

$ErrorActionPreference = 'Stop'

function Invoke-Az {
    $output = az @args 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "az $($args -join ' ') failed:`n$output"
    }
    return $output
}

# --- Preconditions -----------------------------------------------------------

$account = Invoke-Az account show --query '{name:name, subscription:id}' -o json | ConvertFrom-Json
Write-Host "Using subscription: $($account.name) ($($account.subscription))"

if (-not $ResourceGroup) {
    $ResourceGroup = (Invoke-Az functionapp list --query "[?name=='$FunctionAppName'].resourceGroup" -o tsv | Select-Object -First 1)
    if (-not $ResourceGroup) {
        throw "Function app '$FunctionAppName' was not found in subscription '$($account.name)'."
    }
    Write-Host "Resolved resource group: $ResourceGroup"
}

if (-not $PSBoundParameters.ContainsKey('Slots')) {
    $Slots = @(Invoke-Az functionapp deployment slot list -g $ResourceGroup -n $FunctionAppName --query '[].name' -o tsv)
    Write-Host "Discovered slots: $(if ($Slots) { $Slots -join ', ' } else { '(none)' })"
}

$requiredAcmebotSettings = @(
    'Acmebot:SubscriptionId', 'Acmebot:Contacts', 'Acmebot:Endpoint',
    'Acmebot:VaultBaseUrl', 'Acmebot:StorageSasUri'
)

# --- Per-target upgrade ------------------------------------------------------

# $null represents the production app itself; slot names follow.
$targets = @()
if (-not $SkipProduction) { $targets += ,$null }
$targets += $Slots

foreach ($slot in $targets) {
    $label = if ($slot) { "slot '$slot'" } else { 'production app' }
    $slotArgs = if ($slot) { @('--slot', $slot) } else { @() }

    Write-Host ''
    Write-Host "=== $FunctionAppName ($label) ===" -ForegroundColor Cyan

    # Sanity checks on the current state
    $settings = Invoke-Az functionapp config appsettings list -g $ResourceGroup -n $FunctionAppName @slotArgs -o json | ConvertFrom-Json
    $settingsMap = @{}
    foreach ($s in $settings) { $settingsMap[$s.name] = $s.value }

    $extensionVersion = $settingsMap['FUNCTIONS_EXTENSION_VERSION']
    $workerRuntime = $settingsMap['FUNCTIONS_WORKER_RUNTIME']

    Write-Host "Current FUNCTIONS_EXTENSION_VERSION: $extensionVersion"

    if ($workerRuntime -ne 'dotnet') {
        Write-Warning "FUNCTIONS_WORKER_RUNTIME is '$workerRuntime' (expected 'dotnet'). Not changing it - verify this target manually."
    }

    foreach ($required in $requiredAcmebotSettings) {
        if (-not $settingsMap.ContainsKey($required)) {
            Write-Warning "Expected app setting '$required' is missing on this target."
        }
    }

    # 1. Enable .NET 8 in-process support BEFORE flipping the runtime version
    Write-Host 'Setting FUNCTIONS_INPROC_NET8_ENABLED=1 ...'
    Invoke-Az functionapp config appsettings set -g $ResourceGroup -n $FunctionAppName @slotArgs --settings 'FUNCTIONS_INPROC_NET8_ENABLED=1' --output none

    # 2. Flip the Functions runtime to v4
    Write-Host 'Setting FUNCTIONS_EXTENSION_VERSION=~4 ...'
    Invoke-Az functionapp config appsettings set -g $ResourceGroup -n $FunctionAppName @slotArgs --settings 'FUNCTIONS_EXTENSION_VERSION=~4' --output none

    # 3. Set the site .NET version to 8.0
    Write-Host 'Setting site .NET version to v8.0 ...'
    Invoke-Az functionapp config set -g $ResourceGroup -n $FunctionAppName @slotArgs --net-framework-version v8.0 --output none

    Write-Host "Done. This target now requires the v4/.NET 8 payload." -ForegroundColor Green
}

Write-Host ''
Write-Host 'Runtime configuration upgraded on all targets.' -ForegroundColor Green
Write-Host "The old v3 deployment will no longer start - publish the new build now:"
Write-Host "  pwsh -File scripts/build-package.ps1"
Write-Host "  pwsh -File scripts/publish-functionapp.ps1 -FunctionAppName $FunctionAppName"
