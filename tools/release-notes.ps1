<#
.SYNOPSIS
    Writes the release notes body for a TinyWin GitHub Release.

.DESCRIPTION
    A script rather than inline YAML for two reasons. A PowerShell here-string needs its
    closing delimiter at column 0, which no indented YAML block scalar can provide; and the
    notes carry the GPLv3 source statement, which is worth being able to run and read locally
    before a tag is pushed rather than discovering it wrong on a published release.

.PARAMETER Version
    The version being released, without a leading 'v'.

.PARAMETER OutFile
    Where to write the notes. Written to stdout as well if omitted.

.EXAMPLE
    powershell -File tools/release-notes.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [string] $OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$code = [char]0x60  # backtick, escaped so the markdown below stays readable

$lines = @(
    "## TinyWin $Version"
    ''
    "Portable. Download the archive for your architecture, unzip it, and run ${code}TinyWin.exe${code} -"
    'it requests administrator at launch because DISM will not service an image without it.'
    ''
    '| Asset | |'
    '|---|---|'
    "| ${code}TinyWin-$Version-win-x64.zip${code} | Intel/AMD 64-bit. |"
    "| ${code}TinyWin-$Version-win-arm64.zip${code} | Arm64. Ships the x86-64 xorriso build, which runs under Windows on Arm's x64 emulation. |"
    "| ${code}SHA256SUMS.txt${code} | SHA-256 of every archive in this release. |"
    ''
    "Keep the ${code}xorriso\${code} folder next to ${code}TinyWin.exe${code}. Without it TinyWin can only write an ISO"
    'on a machine that already has the Windows ADK installed.'
    ''
    '### Source'
    ''
    'TinyWin is licensed under the **GNU General Public License, version 3 or later**, and'
    'bundles GNU xorriso (GPLv3+), the msys2-runtime (LGPLv3+), GNU libiconv (LGPLv2.1+) and'
    'GNU readline (GPLv3+).'
    ''
    'The complete corresponding source for all of it is attached to this release, as GPLv3'
    'section 6 requires - from the same place as the binaries, not merely linked:'
    ''
    "- ${code}tinywin-$Version-source.zip${code} - TinyWin at this tag, including the scripts and workflows that built these binaries."
    "- ${code}xorriso-bundle-$Version-corresponding-source.zip${code} - upstream source and PKGBUILDs for every bundled native component."
    ''
    "See ${code}THIRD-PARTY.md${code}, included in every archive, for the component-by-component breakdown."
    ''
    '### Scope'
    ''
    'TinyWin never downloads Windows and never touches activation or licensing. It operates'
    'only on media you already have.'
)

$body = $lines -join "`n"

if ($OutFile) {
    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    Set-Content -Path $OutFile -Value $body -Encoding utf8
    Write-Host "Release notes written to $OutFile"
}
else {
    $body
}
