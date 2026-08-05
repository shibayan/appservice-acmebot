#Requires -Version 7.0
<#
.SYNOPSIS
    Phase-2 upgrade of an Acmebot function app target from the in-process model
    (v4 / .NET 8) to the isolated worker model (v4 / .NET 10).

.DESCRIPTION
    App settings and site configuration are per-slot in App Service, so the same
    changes are applied to each selected target:

      1. FUNCTIONS_WORKER_RUNTIME = dotnet-isolated   (was: dotnet)
      2. AcmebotTaskHub           = Acmebot<slot>     (host.json resolves %AcmebotTaskHub%)
      3. Remove FUNCTIONS_INPROC_NET8_ENABLED         (in-process only)
      4. Site config .NET version = v10.0

    NOTE: after this script runs, the currently deployed in-process payload will no
    longer start. Deploy the isolated build immediately afterwards:
    scripts/build-package.ps1 + scripts/publish-functionapp.ps1.

.EXAMPLE
    pwsh -File scripts/upgrade-to-isolated.ps1 -FunctionAppName criipto-io-acme-bot -SkipProduction -Slots buypass

.EXAMPLE
    pwsh -File scripts/upgrade-to-isolated.ps1 -FunctionAppName criipto-io-acme-bot -ProductionOnly
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
    [switch] $SkipProduction,

    # Configure only the production app, ignoring all slots.
    # Unlike '-Slots @()' this also works with 'pwsh -File' invocation.
    [switch] $ProductionOnly
)

$ErrorActionPreference = 'Stop'

if ($ProductionOnly) {
    if ($SkipProduction) { throw '-ProductionOnly and -SkipProduction are mutually exclusive.' }
    if ($PSBoundParameters.ContainsKey('Slots')) { throw 'Use either -ProductionOnly or -Slots, not both.' }
    $Slots = @()
}

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

if (-not $ProductionOnly -and -not $PSBoundParameters.ContainsKey('Slots')) {
    $Slots = @(Invoke-Az functionapp deployment slot list -g $ResourceGroup -n $FunctionAppName --query '[].name' -o tsv)
    Write-Host "Discovered slots: $(if ($Slots) { $Slots -join ', ' } else { '(none)' })"
}

# --- Per-target upgrade ------------------------------------------------------

# $null represents the production app itself; slot names follow.
$targets = @()
if (-not $SkipProduction) { $targets += ,$null }
$targets += $Slots

foreach ($slot in $targets) {
    $label = if ($slot) { "slot '$slot'" } else { 'production app' }
    $slotArgs = if ($slot) { @('--slot', $slot) } else { @() }

    # Task hub names allow only letters and digits; mirrors the naming the
    # in-process Startup.cs used (Acmebotproduction, Acmebotzerossl, ...).
    $slotName = if ($slot) { $slot } else { 'production' }
    $hubName = "Acmebot$(-join ($slotName.ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) }))"

    Write-Host ''
    Write-Host "=== $FunctionAppName ($label) ===" -ForegroundColor Cyan

    $settings = Invoke-Az functionapp config appsettings list -g $ResourceGroup -n $FunctionAppName @slotArgs -o json | ConvertFrom-Json
    $settingsMap = @{}
    foreach ($s in $settings) { $settingsMap[$s.name] = $s.value }

    Write-Host "Current FUNCTIONS_EXTENSION_VERSION: $($settingsMap['FUNCTIONS_EXTENSION_VERSION']), FUNCTIONS_WORKER_RUNTIME: $($settingsMap['FUNCTIONS_WORKER_RUNTIME'])"

    # Works directly from v3 (in-process .NET Core 3.1) as well as from v4 in-process:
    # the isolated model needs no intermediate .NET 8 step and no FUNCTIONS_INPROC_NET8_ENABLED.
    Write-Host "Setting FUNCTIONS_EXTENSION_VERSION=~4, FUNCTIONS_WORKER_RUNTIME=dotnet-isolated and AcmebotTaskHub=$hubName ..."
    Invoke-Az functionapp config appsettings set -g $ResourceGroup -n $FunctionAppName @slotArgs --settings 'FUNCTIONS_EXTENSION_VERSION=~4' 'FUNCTIONS_WORKER_RUNTIME=dotnet-isolated' "AcmebotTaskHub=$hubName" --output none

    if ($settingsMap.ContainsKey('FUNCTIONS_INPROC_NET8_ENABLED')) {
        Write-Host 'Removing FUNCTIONS_INPROC_NET8_ENABLED ...'
        Invoke-Az functionapp config appsettings delete -g $ResourceGroup -n $FunctionAppName @slotArgs --setting-names 'FUNCTIONS_INPROC_NET8_ENABLED' --output none
    }

    Write-Host 'Setting site .NET version to v10.0 ...'
    Invoke-Az functionapp config set -g $ResourceGroup -n $FunctionAppName @slotArgs --net-framework-version v10.0 --output none

    Write-Host "Done. This target now requires the isolated v4/.NET 10 payload." -ForegroundColor Green
}

Write-Host ''
Write-Host 'Runtime configuration upgraded on all targets.' -ForegroundColor Green
Write-Host "The in-process deployment will no longer start - publish the isolated build now:"
Write-Host "  pwsh -File scripts/build-package.ps1"
Write-Host "  pwsh -File scripts/publish-functionapp.ps1 -FunctionAppName $FunctionAppName"
