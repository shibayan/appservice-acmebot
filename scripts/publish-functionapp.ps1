#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes a previously built Acmebot package to a function app and all
    (running) deployment slots via zip deploy.

.DESCRIPTION
    Deploys a package produced by scripts/build-package.ps1. By default the
    package matching the current HEAD commit is used; deploy an older build by
    passing -CommitSha <short-sha>, or any zip directly with -PackagePath.

    - Deploys to the production app and each slot in turn (deployment is
      per-slot; there is no shared deployment).
    - Works with WEBSITE_RUN_FROM_PACKAGE=1 (the zip is stored and run as a package).
    - Stopped slots are skipped with a warning: zip deploy requires the SCM site,
      which is unavailable while a slot is stopped. Start the slot and re-run for
      that slot only (-Slots <name>).

.EXAMPLE
    pwsh -File scripts/publish-functionapp.ps1 -FunctionAppName criipto-io-acme-bot-staging

.EXAMPLE
    # Deploy a specific (non-current) build
    pwsh -File scripts/publish-functionapp.ps1 -FunctionAppName criipto-io-acme-bot-staging -CommitSha 1a2b3c4

.EXAMPLE
    # Deploy to a single slot only, skipping the production app
    pwsh -File scripts/publish-functionapp.ps1 -FunctionAppName criipto-io-acme-bot-staging -Slots test -SkipProduction

.EXAMPLE
    # Deploy only to the production app, leaving all slots untouched
    pwsh -File scripts/publish-functionapp.ps1 -FunctionAppName criipto-io-acme-bot-staging -ProductionOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FunctionAppName,

    # Resolved automatically from the function app name when omitted.
    [string] $ResourceGroup,

    # Slots to deploy to in addition to the production app.
    # Defaults to every deployment slot of the app.
    [string[]] $Slots,

    # Skip deploying to the production app (deploy to slots only).
    [switch] $SkipProduction,

    # Deploy only to the production app, ignoring all slots.
    # Unlike '-Slots @()' this also works with 'pwsh -File' invocation.
    [switch] $ProductionOnly,

    # Short SHA of the build to deploy. Defaults to the current HEAD.
    [string] $CommitSha,

    # Where scripts/build-package.ps1 stores packages.
    [string] $PackageDir = (Join-Path ([IO.Path]::GetTempPath()) 'appservice-acmebot' 'packages'),

    # Explicit package path; overrides -CommitSha/-PackageDir resolution.
    [string] $PackagePath
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

$repoRoot = Split-Path $PSScriptRoot -Parent

# --- Resolve the package to deploy ----------------------------------------------

if (-not $PackagePath) {
    if (-not $CommitSha) {
        $CommitSha = git -C $repoRoot rev-parse --short HEAD
        if ($LASTEXITCODE -ne 0) { throw 'git rev-parse failed and no -CommitSha/-PackagePath was given.' }
    }

    $PackagePath = Join-Path $PackageDir "AppService.Acmebot-$CommitSha.zip"

    if (-not (Test-Path $PackagePath)) {
        # A -dirty package is only deployed when explicitly requested via -PackagePath,
        # since its contents do not correspond to the commit it is named after.
        $available = (Get-ChildItem -Path $PackageDir -Filter 'AppService.Acmebot-*.zip' -ErrorAction SilentlyContinue).Name -join ', '
        throw "No package found for commit $CommitSha at $PackagePath.`nBuild it first: pwsh -File scripts/build-package.ps1`nAvailable packages: $(if ($available) { $available } else { '(none)' })"
    }
}
elseif (-not (Test-Path $PackagePath)) {
    throw "Package not found: $PackagePath"
}

Write-Host "Package: $PackagePath ($([math]::Round((Get-Item $PackagePath).Length / 1MB, 1)) MB)"

# --- Resolve deployment targets --------------------------------------------------

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

$targets = @()
if (-not $SkipProduction) { $targets += ,$null }   # $null represents the production app
$targets += $Slots

# --- Deploy per slot --------------------------------------------------------------

foreach ($slot in $targets) {
    $label = if ($slot) { "slot '$slot'" } else { 'production app' }
    $slotArgs = if ($slot) { @('--slot', $slot) } else { @() }

    Write-Host ''
    Write-Host "=== Deploying to $FunctionAppName ($label) ===" -ForegroundColor Cyan

    $state = Invoke-Az functionapp show -g $ResourceGroup -n $FunctionAppName @slotArgs --query state -o tsv

    if ($state -ne 'Running') {
        Write-Warning "Target is '$state' - skipping deployment (zip deploy needs the SCM site). Start it and re-run with -Slots '$slot'."
        continue
    }

    Invoke-Az functionapp deploy -g $ResourceGroup -n $FunctionAppName @slotArgs --src-path $PackagePath --type zip --output none

    # Smoke check: the v4 host should load and report the functions.
    # 'az functionapp function list' does not support --slot, so query ARM directly.
    $slotPath = if ($slot) { "/slots/$slot" } else { '' }
    $functionsUrl = "https://management.azure.com/subscriptions/$($account.subscription)/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$FunctionAppName$slotPath/functions?api-version=2022-03-01"

    # No --query here: az is a .cmd shim on Windows and cmd.exe cannot pass JMESPath
    # parentheses through unquoted; parse the JSON in PowerShell instead.
    $functionCount = $null
    foreach ($attempt in 1..12) {
        Start-Sleep -Seconds 10
        try {
            $response = Invoke-Az rest --method get --url $functionsUrl
            $functionCount = (($response -join "`n" | ConvertFrom-Json).value).Count
            if ([int]$functionCount -gt 0) { break }
        }
        catch {
            # host may still be starting; retry
        }
    }

    if ([int]$functionCount -gt 0) {
        Write-Host "Deployed. Host is up and reports $functionCount functions." -ForegroundColor Green
    }
    else {
        Write-Warning 'Deployed, but the host did not report any functions yet. Check the target in the portal (Log stream / App Insights).'
    }
}

Write-Host ''
Write-Host 'Publishing finished.' -ForegroundColor Green
