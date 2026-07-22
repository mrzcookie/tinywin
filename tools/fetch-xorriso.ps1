<#
.SYNOPSIS
    Fetches the vendored xorriso bundle into tools/xorriso/.

.DESCRIPTION
    TinyWin ships GNU xorriso as its primary ISO builder (docs/PLAN.md section 3.1). The
    binary is NOT committed to this repository - it is fetched by this script from the
    official MSYS2 package repository, which is where the spike sourced it from and which
    publishes detached signatures alongside every package.

    Why fetched rather than committed: TinyWin is GPLv3, and GPLv3 section 6 requires the
    corresponding source of every copyleft component to be conveyed from the same place as
    the binary. The place we convey binaries is the GitHub Release, not the git tree, so
    the release job runs this script with -WithSource and attaches both the binaries and
    their source packages. Committing a 6.6 MB blob would put it in every clone forever
    without discharging that obligation any better.

    Every package is pinned by version AND SHA-256. A hash mismatch is a hard failure.

.PARAMETER Destination
    Where to place the runnable bundle. Defaults to tools/xorriso next to this script.

.PARAMETER WithSource
    Also download the matching MSYS2 source packages into <Destination>/source. Required
    for release artifacts - see THIRD-PARTY.md.

.PARAMETER Force
    Re-download and re-extract even if the bundle already looks complete.

.EXAMPLE
    pwsh tools/fetch-xorriso.ps1

.EXAMPLE
    pwsh tools/fetch-xorriso.ps1 -WithSource
#>
[CmdletBinding()]
param(
    [string] $Destination,
    [switch] $WithSource,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoBase = 'https://repo.msys2.org/msys/x86_64'
$SourceBase = 'https://repo.msys2.org/msys/sources'

# Pinned MSYS2 packages. xorriso 1.5.6-1 is what docs/spikes/iso-build.md validated;
# the rest are its runtime dependency closure. 'Source' is the pkgbase, which differs
# from the package name for libreadline (readline) and libbz2 (bzip2).
$Packages = @(
    @{ Package = 'xorriso-1.5.6-1-x86_64.pkg.tar.zst'
       Sha256  = '9bd1eb6ea3ae79543e9aba39c0b8a9ffd58d8b20d77ecfc874fac8153dbfee45'
       Source  = 'xorriso-1.5.6-1.src.tar.zst'
       License = 'GPL-3.0-or-later' }
    @{ Package = 'msys2-runtime-3.6.9-2-x86_64.pkg.tar.zst'
       Sha256  = '20f39ad6d0fd2aae93ca84c2e9efbe567d3d7f5d465dc0810b700d7cbac0c3a4'
       Source  = 'msys2-runtime-3.6.9-2.src.tar.zst'
       License = 'LGPL-3.0-or-later (Cygwin fork)' }
    @{ Package = 'libiconv-1.19-1-x86_64.pkg.tar.zst'
       Sha256  = '0fa55ea2a6ccf97cf8c58b24b2615815e15e16e6e4e888091c263c2c83c5313d'
       Source  = 'libiconv-1.19-1.src.tar.zst'
       License = 'LGPL-2.1-or-later' }
    @{ Package = 'ncurses-6.6-2-x86_64.pkg.tar.zst'
       Sha256  = '828c92f09b17f00d3b49362fcfa4affa58e5e50e238f290ea36eb7a690da3acb'
       Source  = 'ncurses-6.6-2.src.tar.zst'
       License = 'MIT-like (X11)' }
    @{ Package = 'libreadline-8.3.003-1-x86_64.pkg.tar.zst'
       Sha256  = 'f78e11a496010305fb80b6804518d5c1257794fc87bc8caaf8c4a65d7a983dec'
       Source  = 'readline-8.3.003-1.src.tar.zst'
       License = 'GPL-3.0-or-later' }
    @{ Package = 'zlib-1.3.2-1-x86_64.pkg.tar.zst'
       Sha256  = 'a04aa79996c57f0db936be66cf94326d7e67e9cd8dbffe4cf6e97693d0a1d9ef'
       Source  = 'zlib-1.3.2-1.src.tar.zst'
       License = 'Zlib' }
    @{ Package = 'libbz2-1.0.8-4-x86_64.pkg.tar.zst'
       Sha256  = '89fb0e1478b22b80effda55ab4ae7549388d01245d3841a096d8a2d63236c2a1'
       Source  = 'bzip2-1.0.8-4.src.tar.zst'
       License = 'bzip2-1.0.6' }
)

# The 8 files the spike proved are sufficient to run xorriso standalone (6.58 MB total).
# Everything else in the packages is left behind deliberately.
$WantedFiles = @(
    'xorriso.exe'
    'msys-2.0.dll'
    'msys-iconv-2.dll'
    'msys-ncursesw6.dll'
    'msys-readline8.dll'
    'msys-z.dll'
    'msys-bz2-1.dll'
    'msys-charset-1.dll'
)

if (-not $Destination) {
    $Destination = Join-Path $PSScriptRoot 'xorriso'
}

$bsdtar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path $bsdtar)) {
    throw "Windows' bundled bsdtar was not found at $bsdtar. It is needed to read .pkg.tar.zst."
}

$complete = $WantedFiles | ForEach-Object { Test-Path (Join-Path $Destination $_) }
if (-not $Force -and ($complete -notcontains $false)) {
    Write-Host "xorriso bundle already present at $Destination (use -Force to refresh)."
    if (-not $WithSource) { return }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("tinywin-xorriso-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

try {
    $staging = Join-Path $work 'extract'
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    foreach ($pkg in $Packages) {
        $archive = Join-Path $work $pkg.Package
        Write-Host "Downloading $($pkg.Package)"
        Invoke-WebRequest -Uri "$RepoBase/$($pkg.Package)" -OutFile $archive -UseBasicParsing -TimeoutSec 300

        $actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $pkg.Sha256) {
            throw "SHA-256 mismatch for $($pkg.Package): expected $($pkg.Sha256), got $actual."
        }

        & $bsdtar -xf $archive -C $staging
        if ($LASTEXITCODE -ne 0) { throw "bsdtar failed to extract $($pkg.Package)." }
    }

    foreach ($file in $WantedFiles) {
        $found = Get-ChildItem -Path $staging -Recurse -File -Filter $file | Select-Object -First 1
        if (-not $found) { throw "'$file' was not found in the extracted packages." }
        Copy-Item -Path $found.FullName -Destination (Join-Path $Destination $file) -Force
    }

    $manifest = @('# Vendored by tools/fetch-xorriso.ps1 from https://repo.msys2.org/msys/x86_64/')
    $manifest += '# TinyWin does not commit these binaries; see THIRD-PARTY.md for the GPLv3 source offer.'
    $manifest += ''
    foreach ($pkg in $Packages) {
        $manifest += ("{0}`n  sha256: {1}`n  source: {2}`n  license: {3}" -f $pkg.Package, $pkg.Sha256, $pkg.Source, $pkg.License)
    }
    Set-Content -Path (Join-Path $Destination 'PACKAGES.txt') -Value ($manifest -join "`n") -Encoding utf8

    if ($WithSource) {
        $srcDir = Join-Path $Destination 'source'
        New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
        foreach ($pkg in $Packages) {
            $out = Join-Path $srcDir $pkg.Source
            Write-Host "Downloading source $($pkg.Source)"
            Invoke-WebRequest -Uri "$SourceBase/$($pkg.Source)" -OutFile $out -UseBasicParsing -TimeoutSec 600
        }
    }

    # xorriso writes its banner to stderr, and redirecting a native command's stderr in
    # Windows PowerShell turns each line into a terminating NativeCommandError. Report the
    # bundle contents instead; TinyWin.IsoBuilder probes the real version at runtime.
    $total = ($WantedFiles | ForEach-Object { (Get-Item (Join-Path $Destination $_)).Length } | Measure-Object -Sum).Sum
    Write-Host ("Bundle ready at {0} - {1} files, {2:N2} MB" -f $Destination, $WantedFiles.Count, ($total / 1MB))
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
