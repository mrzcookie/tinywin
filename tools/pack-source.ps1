<#
.SYNOPSIS
    Builds the two corresponding-source archives that every release must carry.

.DESCRIPTION
    GPLv3 section 6 requires the Corresponding Source to be conveyed from the same place as
    the binary. TinyWin conveys binaries as GitHub Release assets, so the source has to be a
    release asset too - not a link to a repository, and not an offer to send a CD.

    Two archives, because they answer two different questions:

      tinywin-<version>-source.zip
          TinyWin's own source at the released commit, produced with `git archive` so it is
          exactly the tracked tree and nothing else. Includes the workflows and scripts that
          build the binaries, which GPLv3 section 1 counts as Corresponding Source.

      xorriso-bundle-<version>-corresponding-source.zip
          The MSYS2 .src.tar.zst packages for all seven bundled components. Each contains the
          upstream release tarball plus the PKGBUILD that configures, compiles and installs
          it - the "scripts used to control compilation and installation" clause, satisfied
          by the same file the binary was actually built from.

    Roughly 139 MB, of which msys2-runtime is 122 MB. That number is the concrete argument
    for the native mingw-w64 build tracked in docs/PLAN.md section 3.1: a statically linked
    xorriso needs no msys2-runtime and no readline, which would remove both LGPLv3 mirroring
    and most of the payload.

.PARAMETER Version
    Version string used in the archive names. Required for a release; defaults to the same
    git-derived value tools/publish.ps1 uses.

.PARAMETER OutputRoot
    Where the archives are written. Defaults to artifacts/.

.EXAMPLE
    powershell -File tools/pack-source.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SourceDir = Join-Path $PSScriptRoot 'xorriso\source'

if (-not $OutputRoot) { $OutputRoot = Join-Path $RepoRoot 'artifacts' }

if (-not $Version) {
    foreach ($tag in @(& git -C $RepoRoot tag --points-at HEAD)) {
        if ($tag -match '^v?(\d+\.\d+\.\d+(?:[-+].+)?)$') { $Version = $Matches[1]; break }
    }
}
if (-not $Version) {
    $props = Get-Content (Join-Path $RepoRoot 'Directory.Build.props') -Raw
    if ($props -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') {
        throw 'Could not read VersionPrefix from Directory.Build.props.'
    }
    $Version = "$($Matches[1])-dev"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

# --- 1. TinyWin's own source -------------------------------------------------------------

$appSource = Join-Path $OutputRoot "tinywin-$Version-source.zip"
Write-Host "Archiving TinyWin source -> $appSource"

# HEAD, not the working tree: a release must correspond to a commit. --worktree-attributes
# honours .gitattributes so line endings match what CI built from.
& git -C $RepoRoot archive --format=zip --worktree-attributes --prefix="tinywin-$Version/" --output=$appSource HEAD
if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }

# --- 2. The xorriso bundle's corresponding source ----------------------------------------

if (-not (Test-Path $SourceDir)) {
    throw "$SourceDir was not found. Run: tools/fetch-xorriso.ps1 -WithSource"
}

$packages = Get-ChildItem $SourceDir -Filter '*.src.tar.zst' -File
if ($packages.Count -eq 0) {
    throw "No .src.tar.zst packages in $SourceDir. Run: tools/fetch-xorriso.ps1 -WithSource -Force"
}

# The manifest names every package the binaries were built from. If a source package is
# missing, the release would ship a binary with no source - the exact failure GPLv3 section 6
# is about - so this is a hard error rather than a warning.
$manifest = Join-Path $PSScriptRoot 'xorriso\PACKAGES.txt'
if (-not (Test-Path $manifest)) { throw "$manifest was not found. Run tools/fetch-xorriso.ps1." }

$expected = Select-String -Path $manifest -Pattern '^\s*source:\s*(\S+)$' |
    ForEach-Object { $_.Matches[0].Groups[1].Value }
$missing = $expected | Where-Object { $_ -notin $packages.Name }
if ($missing) {
    throw "Corresponding source missing for: $($missing -join ', '). Run: tools/fetch-xorriso.ps1 -WithSource -Force"
}

$staging = Join-Path $OutputRoot "staging\corresponding-source"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$packages | Copy-Item -Destination $staging -Force
Copy-Item $manifest $staging -Force
Copy-Item (Join-Path $RepoRoot 'LICENSE') $staging -Force
Copy-Item (Join-Path $RepoRoot 'THIRD-PARTY.md') $staging -Force

$building = @(
    "Corresponding source for the xorriso bundle shipped with TinyWin $Version"
    '========================================================================'
    ''
    'These are the complete MSYS2 source packages for every binary in the xorriso\ folder of'
    'the TinyWin release archives. Each .src.tar.zst contains the upstream release tarball,'
    'any patches applied, and the PKGBUILD that configures, compiles and installs it.'
    ''
    'PACKAGES.txt lists each binary package, its SHA-256, its source package and its licence.'
    ''
    'To rebuild'
    '----------'
    '  1. Install MSYS2 from https://www.msys2.org/'
    '  2. In an MSYS2 shell:'
    '       tar --use-compress-program=unzstd -xf xorriso-1.5.6-1.src.tar.zst'
    '       cd xorriso'
    '       makepkg -s'
    '  3. The resulting .pkg.tar.zst contains the same xorriso.exe TinyWin ships.'
    ''
    'To reproduce the exact bundle rather than rebuild it, run tools/fetch-xorriso.ps1 from'
    'the TinyWin source archive: it downloads these same packages and verifies each against a'
    'pinned SHA-256.'
    ''
    'Licences'
    '--------'
    'xorriso and readline are GPL-3.0-or-later. msys2-runtime is LGPL-3.0-or-later and'
    'libiconv is LGPL-2.1-or-later. ncurses, zlib and bzip2 are permissive; their source is'
    'mirrored here anyway. Full detail is in THIRD-PARTY.md, included in this archive.'
    ''
    'TinyWin itself is GPL-3.0-or-later; its source is attached to the same release as'
    "tinywin-$Version-source.zip."
)
Set-Content -Path (Join-Path $staging 'BUILDING.txt') -Value ($building -join "`r`n") -Encoding utf8

$bundleSource = Join-Path $OutputRoot "xorriso-bundle-$Version-corresponding-source.zip"
Write-Host "Archiving xorriso corresponding source -> $bundleSource"

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $bundleSource) { Remove-Item $bundleSource -Force }
# Fastest, not Optimal: .src.tar.zst is already zstd-compressed, so Optimal spends minutes to
# save nothing.
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $staging, $bundleSource, [System.IO.Compression.CompressionLevel]::Fastest, $false)

Write-Host ""
foreach ($archive in @($appSource, $bundleSource)) {
    Write-Host ("  {0,-60} {1,8:N1} MB" -f (Split-Path $archive -Leaf), ((Get-Item $archive).Length / 1MB))
}
