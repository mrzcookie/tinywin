<#
.SYNOPSIS
    Captures ground-truth DISM data from real Windows 11 media. REQUIRES ELEVATION.

.DESCRIPTION
    Agent shells are not elevated, so DISM refuses every servicing command with error 740.
    This script is the one thing that must be run by a human in an elevated shell, and it
    unblocks two workstreams at once:

      * catalog-authoring — the definitive package / capability / feature names for build
        26200, instead of names inferred from tiny11's list and Microsoft docs.
      * imaging-engine    — real DISM output samples to write parsers against, plus the
        answer to whether DISM's progress bar survives stdout redirection (which decides
        whether the UI can show real percentages or only stage transitions).

    SAFETY
      * The source ISO is never modified. It is mounted read-only.
      * install.wim is mounted with /ReadOnly, so nothing can be written to the image.
      * The image is always unmounted with /Discard in a finally block, including on Ctrl-C.
      * Nothing is removed, disabled or deleted. Every DISM call here is an enumeration.

.EXAMPLE
    # From an ELEVATED PowerShell, at the repo root:
    powershell -ExecutionPolicy Bypass -File scripts\capture-reference.ps1
#>
[CmdletBinding()]
param(
    [string]$IsoPath = "C:\Users\Zachary\ISOs\Win11_25H2_English_x64_v2.iso",
    [string]$OutputDir = "$PSScriptRoot\..\docs\reference",
    [int]$Index = 1
)

$ErrorActionPreference = 'Stop'

function Write-Step { param($m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Ok   { param($m) Write-Host "  $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  $m" -ForegroundColor Yellow }

# --- Preconditions -----------------------------------------------------------------

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Not elevated. Re-run this from an elevated PowerShell - that is the entire point of the script."
    exit 1
}
Write-Ok "Elevated."

if (-not (Test-Path $IsoPath)) { Write-Error "ISO not found: $IsoPath"; exit 1 }

$mountDir = Join-Path $env:TEMP "tinywin-refmount"
New-Item -ItemType Directory -Force -Path $OutputDir  | Out-Null
New-Item -ItemType Directory -Force -Path $mountDir   | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

# Refuse to run on top of a previous mess rather than compounding it.
$existing = & dism.exe /English /Get-MountedImageInfo 2>&1 | Out-String
if ($existing -notmatch 'No mounted images found') {
    Write-Warn "There are already mounted images:"
    Write-Host $existing
    Write-Warn "Run 'dism /Cleanup-Mountpoints' first, then re-run this script."
    exit 1
}

# --- Mount the ISO (read-only by nature) -------------------------------------------

Write-Step "Mounting ISO"
$alreadyMounted = Get-DiskImage -ImagePath $IsoPath | Where-Object { $_.Attached }
if ($alreadyMounted) {
    $drive = ($alreadyMounted | Get-Volume).DriveLetter
    Write-Ok "Already mounted at ${drive}:"
    $weMountedIso = $false
} else {
    $img = Mount-DiskImage -ImagePath $IsoPath -PassThru
    $drive = ($img | Get-Volume).DriveLetter
    Write-Ok "Mounted at ${drive}:"
    $weMountedIso = $true
}

$wim = "${drive}:\sources\install.wim"
if (-not (Test-Path $wim)) { $wim = "${drive}:\sources\install.esd" }
if (-not (Test-Path $wim)) { Write-Error "No install.wim or install.esd under ${drive}:\sources"; exit 1 }
Write-Ok "Image: $wim ($([math]::Round((Get-Item $wim).Length/1GB,2)) GB)"

$mounted = $false
try {
    # --- Edition list (no mount needed) --------------------------------------------

    Write-Step "Capturing edition list"
    & dism.exe /English /Get-WimInfo /WimFile:$wim > "$OutputDir\01-wiminfo.txt" 2>&1
    Write-Ok "01-wiminfo.txt"

    # --- Progress-bar encoding under redirection -----------------------------------
    # The open question from docs/spikes/dism-backend.md section 5: when stdout is a PIPE
    # rather than a console, does DISM emit backspaces, bare CR, or suppress the bar?
    # This decides whether DismExeBackend can report real percentages.

    Write-Step "Probing progress-bar encoding under redirection"
    $probe = "$env:TEMP\tinywin-progress-probe.bin"
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = "dism.exe"
    $psi.Arguments = "/English /Get-WimInfo /WimFile:`"$wim`" /Index:$Index"
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    $raw = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    [IO.File]::WriteAllText($probe, $raw)

    $bytes = [Text.Encoding]::UTF8.GetBytes($raw)
    $bs = ($bytes | Where-Object { $_ -eq 0x08 }).Count
    $cr = ($bytes | Where-Object { $_ -eq 0x0D }).Count
    $lf = ($bytes | Where-Object { $_ -eq 0x0A }).Count
    @"
DISM progress encoding under stdout redirection
===============================================
backspace (0x08) count : $bs
CR        (0x0D) count : $cr
LF        (0x0A) count : $lf

Interpretation:
  backspace > 0  -> progress bar is \b-driven and parseable incrementally
  CR >> LF       -> bar rewrites the line with bare \r
  both ~0        -> bar is suppressed when not a console; DismExeBackend can only
                    report stage transitions, not real percentages
"@ | Set-Content "$OutputDir\02-progress-encoding.txt" -Encoding utf8
    Write-Ok "02-progress-encoding.txt  (backspaces=$bs cr=$cr lf=$lf)"

    # --- Mount READ-ONLY ------------------------------------------------------------

    Write-Step "Mounting image index $Index (read-only, this takes a few minutes)"
    & dism.exe /English /Mount-Wim /WimFile:$wim /Index:$Index /MountDir:$mountDir /ReadOnly
    if ($LASTEXITCODE -ne 0) { Write-Error "Mount failed with exit code $LASTEXITCODE"; exit 1 }
    $mounted = $true
    Write-Ok "Mounted read-only at $mountDir"

    # --- The ground truth the catalog agent needs -----------------------------------

    $captures = @(
        @{ File = "03-provisioned-appx.txt"; Args = "/Get-ProvisionedAppxPackages" },
        @{ File = "04-capabilities.txt";     Args = "/Get-Capabilities" },
        @{ File = "05-features.txt";         Args = "/Get-Features" },
        @{ File = "06-packages.txt";         Args = "/Get-Packages" }
    )

    foreach ($c in $captures) {
        Write-Step "Capturing $($c.Args)"
        & dism.exe /English /Image:$mountDir $($c.Args) > "$OutputDir\$($c.File)" 2>&1
        $lines = (Get-Content "$OutputDir\$($c.File)" | Measure-Object -Line).Lines
        Write-Ok "$($c.File)  ($lines lines)"
    }

    # --- Filesystem facts the catalog needs -----------------------------------------

    Write-Step "Capturing scheduled tasks and long filenames"
    Get-ChildItem "$mountDir\Windows\System32\Tasks" -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName.Replace("$mountDir\Windows\System32\Tasks", '') } |
        Set-Content "$OutputDir\07-scheduled-tasks.txt" -Encoding utf8
    Write-Ok "07-scheduled-tasks.txt"

    # Joliet caps basenames at 103 chars - does real media exceed it? (spike section 6)
    $maxName = Get-ChildItem "${drive}:\" -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object { $_.Name.Length } -Descending | Select-Object -First 10
    $maxName | ForEach-Object { "{0,4}  {1}" -f $_.Name.Length, $_.Name } |
        Set-Content "$OutputDir\08-longest-basenames.txt" -Encoding utf8
    $longest = ($maxName | Select-Object -First 1).Name.Length
    if ($longest -gt 103) { Write-Warn "Longest basename is $longest chars - EXCEEDS the 103-char Joliet limit!" }
    else { Write-Ok "08-longest-basenames.txt  (longest = $longest chars, within the 103 limit)" }
}
finally {
    if ($mounted) {
        Write-Step "Unmounting image (discard)"
        & dism.exe /English /Unmount-Wim /MountDir:$mountDir /Discard | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Ok "Unmounted cleanly." }
        else { Write-Warn "Unmount returned $LASTEXITCODE - run 'dism /Cleanup-Mountpoints'." }
    }
    if ($weMountedIso) {
        Dismount-DiskImage -ImagePath $IsoPath | Out-Null
        Write-Ok "ISO dismounted."
    }
}

# --- Environment check for the later boot test --------------------------------------

Write-Step "Hyper-V availability (needed for the ISO boot test)"
$hv = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -ErrorAction SilentlyContinue
if ($hv) { Write-Ok "Microsoft-Hyper-V-All: $($hv.State)" } else { Write-Warn "Could not query Hyper-V state." }

Write-Host "`nDone. Reference data written to:" -ForegroundColor Cyan
Write-Host "  $OutputDir`n"
Get-ChildItem $OutputDir | Format-Table Name, Length -AutoSize
