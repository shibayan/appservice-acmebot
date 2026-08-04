#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the Acmebot deployment package locally.

.DESCRIPTION
    Runs 'dotnet publish' locally (initializing the ACMESharpCore submodule if
    needed) and zips the output to:

        <OutputDir>\AppService.Acmebot-<short-sha>.zip

    The short commit SHA of the current HEAD is embedded in the file name so that
    scripts/publish-functionapp.ps1 can later deploy a specific build. If the
    working tree has uncommitted changes, '-dirty' is appended to the name, since
    the package contents then do not correspond to the commit.

.EXAMPLE
    pwsh -File scripts/build-package.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',

    # Where packages are stored. Must match the -PackageDir used when publishing.
    [string] $OutputDir = (Join-Path ([IO.Path]::GetTempPath()) 'appservice-acmebot' 'packages')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

# --- Determine the package name from the current commit ------------------------

$sha = git -C $repoRoot rev-parse --short HEAD
if ($LASTEXITCODE -ne 0) { throw 'git rev-parse failed - is this a git repository?' }

$dirty = [bool](git -C $repoRoot status --porcelain)
if ($dirty) {
    Write-Warning 'The working tree has uncommitted changes; the package will be suffixed with -dirty.'
}

$packageName = "AppService.Acmebot-$sha$(if ($dirty) { '-dirty' }).zip"
$packagePath = Join-Path $OutputDir $packageName

# --- Build ----------------------------------------------------------------------

# The project references ACMESharpCore as a git submodule
if (-not (Test-Path (Join-Path $repoRoot 'ACMESharpCore' 'src' 'ACMESharp' 'ACMESharp.csproj'))) {
    Write-Host 'Initializing ACMESharpCore submodule ...'
    git -C $repoRoot submodule update --init
    if ($LASTEXITCODE -ne 0) { throw 'git submodule update --init failed.' }
}

$publishDir = Join-Path ([IO.Path]::GetTempPath()) 'appservice-acmebot' 'publish'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host "Publishing ($Configuration) at commit $sha ..."
dotnet publish (Join-Path $repoRoot 'AppService.Acmebot' 'AppService.Acmebot.csproj') -c $Configuration -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# --- Package --------------------------------------------------------------------

New-Item -ItemType Directory -Force $OutputDir | Out-Null
if (Test-Path $packagePath) { Remove-Item $packagePath -Force }

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $packagePath

Write-Host "Created $packagePath ($([math]::Round((Get-Item $packagePath).Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "Deploy it with:"
Write-Host "  pwsh -File scripts/publish-functionapp.ps1 -FunctionAppName <app> -CommitSha $sha$(if ($dirty) { ' # note: -dirty package' })"

# Output the path as the script result, for composability
$packagePath
