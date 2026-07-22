<#
.SYNOPSIS
    Verifies TinyWin.Imaging's DismExeBackend against real DISM. REQUIRES ELEVATION.

.DESCRIPTION
    dism.exe performs its elevation check before parsing arguments, so an unelevated agent shell
    gets error 740 from every command including /Get-WimInfo. Everything in
    tests/TinyWin.Imaging.Tests therefore runs against captured output behind an IProcessRunner
    seam. This script closes the remaining gap, in three phases:

      1. CAPTURE   Real output for every enumeration command, written to
                   tests/TinyWin.Imaging.Tests/Samples/captured/. Diff it against the checked-in
                   samples; a difference means the parser was tested against a shape DISM does not
                   actually produce. Read-only, no mount.

      2. PROBE     Answers the question docs/spikes/dism-backend.md section 5 left open: when stdout
                   is a pipe rather than a console, does DISM's progress bar use backspaces, a bare
                   carriage return, or vanish entirely? Probed on /Mount-Wim, which is long enough
                   to actually draw one. (scripts/capture-reference.ps1 probes /Get-WimInfo, which
                   may be too fast to draw anything.)

      3. VERIFY    Runs the elevated xunit tests, which drive the real DismExeBackend end to end:
                   mount, enumerate, remove something present, remove something absent, unmount.

    SAFETY
      * The source WIM is only ever mounted /ReadOnly and unmounted with /Discard.
      * Phase 3 needs a writable image, so it exports one index to a scratch WIM under -WorkDir and
        works on that. The source is never opened for writing.
      * Every mount is unmounted with /Discard in a finally block, including on Ctrl-C.
      * Phase 3 is opt-in via -Full because the export costs time and ~10 GB.

.PARAMETER Wim
    Source install.wim. Defaults to the 25H2 media mounted at D:.

.PARAMETER Index
    Image index to use for the mount-based phases. Defaults to 1 (Home) as the smallest.

.PARAMETER Full
    Also run phase 3. Requires roughly 10 GB free under -WorkDir and takes tens of minutes.

.EXAMPLE
    # From an ELEVATED PowerShell, at the repo root. Phases 1-2 only, a few minutes:
    powershell -ExecutionPolicy Bypass -File scripts\Verify-DismBackend.ps1

.EXAMPLE
    # Everything, including real removals against a scratch copy:
    powershell -ExecutionPolicy Bypass -File scripts\Verify-DismBackend.ps1 -Full
#>
[CmdletBinding()]
param(
    [string]$Wim = 'D:\sources\install.wim',
    [int]$Index = 1,
    [string]$WorkDir = "$env:TEMP\tinywin-verify",
    [switch]$Full
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$captureDir = Join-Path $repo 'tests\TinyWin.Imaging.Tests\Samples\captured'
$testProject = Join-Path $repo 'tests\TinyWin.Imaging.Tests\TinyWin.Imaging.Tests.csproj'

function Write-Step { param($m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Ok   { param($m) Write-Host "  [ok]   $m" -ForegroundColor Green }
function Write-Info { param($m) Write-Host "  $m" -ForegroundColor Gray }
function Write-Warn { param($m) Write-Host "  [warn] $m" -ForegroundColor Yellow }

# --- Preconditions ------------------------------------------------------------------

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Not elevated. DISM refuses every command with error 740 without it - that refusal is the entire reason this script exists. Re-run from an elevated PowerShell."
    exit 1
}

if (-not (Test-Path $Wim)) {
    Write-Error "No WIM at '$Wim'. Mount the Windows 11 ISO and pass -Wim <path to install.wim>."
    exit 1
}

New-Item -ItemType Directory -Force -Path $captureDir, $WorkDir | Out-Null
$captureDir = (Resolve-Path $captureDir).Path
$WorkDir = (Resolve-Path $WorkDir).Path

Write-Host "TinyWin.Imaging - DISM backend verification" -ForegroundColor White
Write-Info "wim      : $Wim ($([math]::Round((Get-Item $Wim).Length / 1GB, 2)) GB)"
Write-Info "index    : $Index"
Write-Info "captures : $captureDir"
Write-Info "workdir  : $WorkDir"

# Always /English. A parser keyed on English output silently finds nothing against a localised
# DISM, which is a no-op bug of exactly the class CLAUDE.md forbids.
function Invoke-Dism {
    param([string]$Arguments, [string]$OutFile)

    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'dism.exe'
    $psi.Arguments = "/English $Arguments"
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()

    if ($OutFile) { [IO.File]::WriteAllText((Join-Path $captureDir $OutFile), $out + $err) }
    [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $out + $err }
}

$mountDir = Join-Path $WorkDir 'mount'
$mounted = $false

try {
    # --- Phase 1: capture, read-only, no mount --------------------------------------

    Write-Step 'Phase 1 - capturing enumeration output'

    $r = Invoke-Dism "/Get-WimInfo /WimFile:`"$Wim`"" 'get-wiminfo-list.txt'
    if ($r.ExitCode -ne 0) { Write-Error "Get-WimInfo failed with $($r.ExitCode):`n$($r.Output)"; exit 1 }
    Write-Ok 'get-wiminfo-list.txt'

    Invoke-Dism "/Get-WimInfo /WimFile:`"$Wim`" /Index:$Index" "get-wiminfo-index$Index.txt" | Out-Null
    Write-Ok "get-wiminfo-index$Index.txt"

    Invoke-Dism '/Get-MountedWimInfo' 'get-mountedwiminfo.txt' | Out-Null
    Write-Ok 'get-mountedwiminfo.txt'

    # --- Phase 2: mount read-only, capture image enumerations, probe progress --------

    Write-Step 'Phase 2 - progress encoding under redirection'

    # The bar is only interesting on a long operation. /Mount-Wim on a 25H2 index takes minutes.
    New-Item -ItemType Directory -Force -Path $mountDir | Out-Null
    $raw = Join-Path $WorkDir 'mount-stdout.bin'

    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'dism.exe'
    $psi.Arguments = "/English /Mount-Wim /WimFile:`"$Wim`" /Index:$Index /MountDir:`"$mountDir`" /ReadOnly"
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    Write-Info 'mounting /ReadOnly (this takes a few minutes)...'
    $p = [Diagnostics.Process]::Start($psi)
    $bytes = New-Object Collections.Generic.List[byte]
    $buffer = New-Object byte[] 4096
    $stream = $p.StandardOutput.BaseStream
    while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $bytes.AddRange($buffer[0..($read - 1)])
    }
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { Write-Error "Mount failed with $($p.ExitCode)."; exit 1 }
    $mounted = $true
    [IO.File]::WriteAllBytes($raw, $bytes.ToArray())

    $bs = ($bytes | Where-Object { $_ -eq 0x08 }).Count
    $cr = ($bytes | Where-Object { $_ -eq 0x0D }).Count
    $lf = ($bytes | Where-Object { $_ -eq 0x0A }).Count
    $text = [Text.Encoding]::UTF8.GetString($bytes.ToArray())
    $pct = ([regex]::Matches($text, '\d{1,3}[.,]\d+\s*%')).Count

    $verdict = if ($pct -eq 0) {
        'SUPPRESSED - no percentage reached the pipe. DismOutputReader.SawPercentage stays false and DismStageProgress falls back to stage transitions. This is the case that fallback exists for.'
    } elseif ($bs -gt 0) {
        'BACKSPACE-DRIVEN - DismOutputReader erases on 0x08 and decodes every repaint.'
    } elseif ($cr -gt $lf) {
        'CR-DRIVEN - bare carriage returns. DismOutputReader flushes on 0x0D and decodes every repaint.'
    } else {
        'LINE-BASED - percentages arrive as ordinary lines. Decoded on flush.'
    }

    @"
DISM progress-bar encoding under stdout redirection
===================================================
Probed on : /Mount-Wim /Index:$Index /ReadOnly
Captured  : $raw ($($bytes.Count) bytes)

backspace (0x08) : $bs
CR        (0x0D) : $cr
LF        (0x0A) : $lf
percentages seen : $pct

VERDICT: $verdict

All three encodings are handled by DismOutputReader (see DismOutputReaderTests). This file records
which one this build of DISM actually uses, which is what docs/spikes/dism-backend.md section 5
could not determine unelevated.
"@ | Set-Content (Join-Path $captureDir 'progress-encoding.txt') -Encoding utf8

    Write-Ok "progress-encoding.txt  (backspace=$bs cr=$cr lf=$lf percentages=$pct)"
    Write-Info $verdict

    Write-Step 'Phase 2 - capturing image enumerations'
    foreach ($c in @(
            @{ Args = '/Get-ProvisionedAppxPackages'; File = 'get-provisionedappxpackages.txt' },
            @{ Args = '/Get-Capabilities';            File = 'get-capabilities.txt' },
            @{ Args = '/Get-Features';                File = 'get-features.txt' },
            @{ Args = '/Get-Packages';                File = 'get-packages.txt' })) {

        $r = Invoke-Dism "/Image:`"$mountDir`" $($c.Args)" $c.File
        if ($r.ExitCode -ne 0) { Write-Warn "$($c.Args) exited $($r.ExitCode)" }
        else { Write-Ok $c.File }
    }

    Invoke-Dism "/Get-MountedWimInfo" 'get-mountedwiminfo-while-mounted.txt' | Out-Null
    Write-Ok 'get-mountedwiminfo-while-mounted.txt'

    Write-Step 'Unmounting /Discard'
    $r = Invoke-Dism "/Unmount-Wim /MountDir:`"$mountDir`" /Discard"
    if ($r.ExitCode -ne 0) { Write-Warn "Unmount exited $($r.ExitCode). Run dism /Cleanup-Mountpoints." }
    else { $mounted = $false; Write-Ok 'unmounted' }

    # --- Phase 3: end-to-end against the real backend --------------------------------

    $env:TINYWIN_ELEVATED_WIM = $Wim

    if ($Full) {
        Write-Step 'Phase 3 - exporting a scratch WIM'
        $scratch = Join-Path $WorkDir 'scratch-install.wim'
        if (Test-Path $scratch) { Remove-Item $scratch -Force }

        Write-Info "exporting index $Index to $scratch (this takes a while)..."
        $r = Invoke-Dism "/Export-Image /SourceImageFile:`"$Wim`" /SourceIndex:$Index /DestinationImageFile:`"$scratch`" /Compress:max"
        if ($r.ExitCode -ne 0) { Write-Error "Export-Image failed with $($r.ExitCode):`n$($r.Output)"; exit 1 }
        Write-Ok "scratch WIM ready ($([math]::Round((Get-Item $scratch).Length / 1GB, 2)) GB)"

        $env:TINYWIN_ELEVATED_RW_WIM = $scratch
    }
    else {
        Write-Info 'Skipping phase 3 (pass -Full to run the mount/remove/unmount tests).'
    }

    Write-Step 'Running the elevated backend tests'
    & dotnet test $testProject --nologo -v q --logger 'console;verbosity=detailed' `
        --filter 'FullyQualifiedName~ElevatedBackendTests'
    if ($LASTEXITCODE -ne 0) { Write-Warn "Elevated tests reported failures (exit $LASTEXITCODE)." }
    else { Write-Ok 'elevated tests passed' }
}
finally {
    if ($mounted) {
        Write-Step 'Cleaning up a mount left behind'
        Invoke-Dism "/Unmount-Wim /MountDir:`"$mountDir`" /Discard" | Out-Null
        Invoke-Dism '/Cleanup-Mountpoints' | Out-Null
    }

    Remove-Item Env:\TINYWIN_ELEVATED_WIM -ErrorAction SilentlyContinue
    Remove-Item Env:\TINYWIN_ELEVATED_RW_WIM -ErrorAction SilentlyContinue
}

Write-Step 'Next steps'
Write-Host @"
  1. Diff the captures against the checked-in samples:

       git diff --no-index tests\TinyWin.Imaging.Tests\Samples tests\TinyWin.Imaging.Tests\Samples\captured

     Any difference in the block structure is a finding, not noise - it means DismOutputParser was
     tested against a shape DISM does not produce. Replace the sample and re-run the unit tests.

  2. Read captured\progress-encoding.txt. It answers docs/spikes/dism-backend.md section 5, and is
     worth folding back into that document.
"@ -ForegroundColor Gray
