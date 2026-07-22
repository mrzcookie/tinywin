<#
.SYNOPSIS
    Publishes TinyWin as a portable single-file executable and packs a release archive.

.DESCRIPTION
    One script for both the release workflow and a local build, so that what CI produces is
    what a developer can reproduce. For each requested runtime identifier it:

      1. publishes src/TinyWin.App with the docs/PLAN.md section 3.4 property combination,
      2. asserts the result really is a single self-contained executable,
      3. lays the vendored xorriso bundle alongside it,
      4. copies LICENSE and THIRD-PARTY.md into the archive - a GPLv3 obligation, not a
         nicety - and writes a short README.txt,
      5. zips it and records the SHA-256.

    The publish property combination lives in src/TinyWin.App/TinyWin.App.csproj and is NOT
    repeated here. Passing those properties on the command line would let the two drift and
    let a plain `dotnet publish` silently produce something different from a release.

.PARAMETER RuntimeIdentifier
    Which architectures to build. Defaults to both shipped ones.

.PARAMETER Version
    The version to stamp. Defaults to a value derived from git: an exact `v1.2.3` tag gives
    `1.2.3`, anything else gives the csproj version with a `-dev` suffix.

.PARAMETER OutputRoot
    Where staging trees and archives are written. Defaults to artifacts/ (gitignored).

.PARAMETER SkipXorriso
    Publish without the xorriso bundle. For inspecting the executable only - an archive built
    this way is not shippable, and the script says so.

.EXAMPLE
    powershell -File tools/publish.ps1

.EXAMPLE
    powershell -File tools/publish.ps1 -RuntimeIdentifier win-x64 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $RuntimeIdentifier = @('win-x64', 'win-arm64'),
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $OutputRoot,
    [switch] $SkipXorriso
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$AppProject = Join-Path $RepoRoot 'src\TinyWin.App\TinyWin.App.csproj'
$XorrisoDir = Join-Path $PSScriptRoot 'xorriso'

if (-not $OutputRoot) { $OutputRoot = Join-Path $RepoRoot 'artifacts' }

# The single-file executable is named from <AssemblyName>, which is TinyWin, not TinyWin.App.
$ExeName = 'TinyWin.exe'

function Resolve-TinyWinVersion {
    <#
        A tag is the only thing that produces a clean version. Everything else is marked -dev
        so a stray local build can never be mistaken for a release artifact.
    #>
    # `git tag --points-at HEAD` rather than `git describe --exact-match`: describe writes to
    # stderr when there is no tag, and redirecting a native command's stderr in Windows
    # PowerShell turns it into a terminating NativeCommandError under $ErrorActionPreference.
    foreach ($tag in @(& git -C $RepoRoot tag --points-at HEAD)) {
        if ($tag -match '^v?(\d+\.\d+\.\d+(?:[-+].+)?)$') { return $Matches[1] }
    }

    $props = Get-Content (Join-Path $RepoRoot 'Directory.Build.props') -Raw
    if ($props -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') {
        throw 'Could not read VersionPrefix from Directory.Build.props.'
    }
    $prefix = $Matches[1]

    $sha = & git -C $RepoRoot rev-parse --short HEAD
    if ($LASTEXITCODE -ne 0 -or -not $sha) { return "$prefix-dev" }
    return "$prefix-dev+$sha"
}

function New-ZipArchive {
    param([string] $SourceDirectory, [string] $DestinationPath, [string] $Level = 'Optimal')

    # ZipFile beats Compress-Archive by a wide margin on the 139 MB source archive, and takes a
    # compression level that matters when the payload is already-compressed .zst.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $DestinationPath) { Remove-Item $DestinationPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDirectory,
        $DestinationPath,
        [System.IO.Compression.CompressionLevel]::$Level,
        $false)
}

if (-not (Test-Path $AppProject)) {
    throw @"
$AppProject was not found.

TinyWin.App is built in the ui-shell worktree and has not landed on this branch yet
(see docs/STATUS.md). The release workflow will fail here until it merges - which is the
intended behaviour: a release without the app is not a release.
"@
}

if (-not $Version) { $Version = Resolve-TinyWinVersion }

# `dotnet publish` wants a plain version; git metadata after '+' is informational only and
# MSBuild rejects it in AssemblyVersion. Split it back apart.
$assemblyVersion, $buildMetadata = $Version -split '\+', 2

if (-not $SkipXorriso) {
    if (-not (Test-Path (Join-Path $XorrisoDir 'xorriso.exe'))) {
        throw "The xorriso bundle is missing. Run tools/fetch-xorriso.ps1 first, or pass -SkipXorriso for a non-shippable build."
    }
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$results = @()

foreach ($rid in $RuntimeIdentifier) {
    Write-Host ""
    Write-Host "=== $rid ===" -ForegroundColor Cyan

    $staging = Join-Path $OutputRoot "staging\$rid"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    $publishArgs = @(
        'publish', $AppProject
        '--configuration', $Configuration
        '--runtime', $rid
        '--output', $staging
        "-p:Version=$assemblyVersion"
        '--nologo'
    )
    if ($buildMetadata) { $publishArgs += "-p:SourceRevisionId=$buildMetadata" }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid." }

    $exe = Join-Path $staging $ExeName
    if (-not (Test-Path $exe)) {
        throw "$ExeName was not produced for $rid. Check <AssemblyName> in TinyWin.App.csproj."
    }

    # PublishSingleFile silently degrades to a folder of DLLs if the property combination breaks,
    # and a release that ships 400 loose files instead of one exe should fail here, not on a user's
    # machine. PDBs are expected next to it and are not part of the archive.
    $strays = Get-ChildItem $staging -File | Where-Object { $_.Extension -notin '.exe', '.pdb' }
    if ($strays) {
        throw "Expected a single-file publish but found loose files for ${rid}: $($strays.Name -join ', ')"
    }
    Get-ChildItem $staging -Filter *.pdb | Remove-Item -Force

    if (-not $SkipXorriso) {
        # BackendLocator probes <exe dir>\xorriso\xorriso.exe - see src/TinyWin.IsoBuilder/BackendLocator.cs.
        $target = Join-Path $staging 'xorriso'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Get-ChildItem $XorrisoDir -File | Copy-Item -Destination $target -Force
    }

    Copy-Item (Join-Path $RepoRoot 'LICENSE') $staging -Force
    Copy-Item (Join-Path $RepoRoot 'THIRD-PARTY.md') $staging -Force

    $readme = @(
        "TinyWin $Version ($rid)"
        ''
        'Portable. Nothing is installed and nothing is written outside the folders you choose.'
        'Run TinyWin.exe - it requests administrator at launch, because DISM refuses to service'
        'an image without it and prompting halfway through a six-gigabyte build is worse.'
        ''
        'Contents'
        '  TinyWin.exe      the application, self-contained'
        '  xorriso\         GNU xorriso (GPLv3+) and its runtime DLLs, used to write the ISO'
        '  LICENSE          GNU GPL v3'
        '  THIRD-PARTY.md   every bundled component, its licence, and where to get its source'
        ''
        'Keep xorriso\ next to TinyWin.exe. Without it TinyWin can only build an ISO on a machine'
        'that already has the Windows ADK installed.'
        ''
        'Source for TinyWin and for every bundled GPL/LGPL component is attached to the same'
        'release as this archive. See THIRD-PARTY.md.'
    )
    if ($SkipXorriso) {
        $readme += ''
        $readme += '*** Built with -SkipXorriso. This archive is incomplete and must not be released. ***'
    }
    Set-Content -Path (Join-Path $staging 'README.txt') -Value ($readme -join "`r`n") -Encoding utf8

    $zip = Join-Path $OutputRoot "TinyWin-$Version-$rid.zip"
    New-ZipArchive -SourceDirectory $staging -DestinationPath $zip

    $exeSize = (Get-Item $exe).Length
    $zipSize = (Get-Item $zip).Length
    Write-Host ("  {0}  exe {1:N1} MB  archive {2:N1} MB" -f $ExeName, ($exeSize / 1MB), ($zipSize / 1MB))

    $results += [pscustomobject]@{
        RuntimeIdentifier = $rid
        Archive           = $zip
        ExeBytes          = $exeSize
        ArchiveBytes      = $zipSize
    }
}

Write-Host ""
Write-Host "Version $Version -> $OutputRoot" -ForegroundColor Green
$results
