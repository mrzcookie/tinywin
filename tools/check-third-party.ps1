<#
.SYNOPSIS
    Fails if a bundled third-party component is not recorded in THIRD-PARTY.md.

.DESCRIPTION
    The GPLv3 source obligation is only discharged for components we know we ship. A package
    added to tools/fetch-xorriso.ps1 without a matching entry in THIRD-PARTY.md would be
    conveyed with no attribution and no source offer, and nothing else in the build would
    notice - the release would look complete.

    So this is a build step, not a review checklist. It runs in CI and again in the release
    job, needs no network, and takes no position on wording: it checks that every pinned
    package name, version, licence and source package can be accounted for.

.PARAMETER ThirdPartyPath
    Defaults to THIRD-PARTY.md at the repository root.

.EXAMPLE
    powershell -File tools/check-third-party.ps1
#>
[CmdletBinding()]
param(
    [string] $ThirdPartyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$FetchScript = Join-Path $PSScriptRoot 'fetch-xorriso.ps1'

if (-not $ThirdPartyPath) { $ThirdPartyPath = Join-Path $RepoRoot 'THIRD-PARTY.md' }

foreach ($path in @($FetchScript, $ThirdPartyPath)) {
    if (-not (Test-Path $path)) { throw "$path was not found." }
}

$document = Get-Content $ThirdPartyPath -Raw
$failures = [System.Collections.Generic.List[string]]::new()

# Parse the pinned package table out of fetch-xorriso.ps1 by reading its source rather than
# running it: the script downloads 145 MB, and this check must work offline in CI.
$fetchText = Get-Content $FetchScript -Raw
$entries = [regex]::Matches(
    $fetchText,
    "Package\s*=\s*'(?<package>[^']+)'\s*\r?\n\s*Sha256\s*=\s*'(?<sha>[0-9a-f]{64})'\s*\r?\n\s*Source\s*=\s*'(?<source>[^']+)'\s*\r?\n\s*License\s*=\s*'(?<license>[^']+)'")

if ($entries.Count -eq 0) {
    throw "No pinned packages could be parsed from $FetchScript. The `$Packages table format changed - update this checker rather than deleting it."
}

Write-Host "Checking $($entries.Count) pinned packages against $(Split-Path $ThirdPartyPath -Leaf)"

foreach ($entry in $entries) {
    # xorriso-1.5.6-1-x86_64.pkg.tar.zst -> name 'xorriso', version '1.5.6'
    $package = $entry.Groups['package'].Value
    if ($package -notmatch '^(?<name>.+?)-(?<version>[0-9][^-]*)-(?<rel>[^-]+)-x86_64\.pkg\.tar\.zst$') {
        $failures.Add("Could not parse a name and version out of '$package'.")
        continue
    }

    $name = $Matches['name']
    $version = $Matches['version']

    # The document names components as people know them, not as MSYS2 packages them:
    # libreadline is readline, libbz2 is bzip2. The source package already carries that name.
    $sourceName = ($entry.Groups['source'].Value -replace '-[0-9][^-]*-[^-]+\.src\.tar\.zst$', '')

    if ($document -notmatch [regex]::Escape($sourceName)) {
        $failures.Add("'$sourceName' (from $package) is not mentioned in THIRD-PARTY.md.")
    }

    if ($document -notmatch [regex]::Escape($version)) {
        $failures.Add("Version '$version' of $sourceName is not recorded in THIRD-PARTY.md.")
    }

    # Licence identifiers, not prose. Trailing parenthetical notes in the pin are ignored.
    $license = ($entry.Groups['license'].Value -replace '\s*\(.*\)$', '')
    if ($document -notmatch [regex]::Escape($license)) {
        $failures.Add("Licence '$license' for $sourceName is not stated in THIRD-PARTY.md.")
    }
}

# The source offer itself. Without these, the file lists components but conveys nothing.
$required = @{
    'the GPLv3 section 6 source-offer heading' = 'Where to get the source'
    'the TinyWin source asset name'            = 'tinywin-<version>-source.zip'
    'the bundle source asset name'             = 'xorriso-bundle-<version>-corresponding-source.zip'
}
foreach ($item in $required.GetEnumerator()) {
    if ($document -notmatch [regex]::Escape($item.Value)) {
        $failures.Add("THIRD-PARTY.md is missing $($item.Key) ('$($item.Value)').")
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    foreach ($failure in $failures) { Write-Host "  FAIL  $failure" -ForegroundColor Red }
    Write-Host ''
    throw "$($failures.Count) third-party compliance problem(s). TinyWin ships GPLv3 and LGPLv3 code; an unrecorded component is a licence violation, not a documentation gap."
}

Write-Host "OK - every pinned component is recorded with its version, licence and source offer." -ForegroundColor Green
