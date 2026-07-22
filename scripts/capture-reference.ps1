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
    [string]$OutputDir,
    [int]$Index = 1
)

$ErrorActionPreference = 'Stop'

# Resolved here, not as a parameter default. Under Windows PowerShell 5.1 $PSScriptRoot is not
# reliably populated while the param block is being bound, and an empty value silently turned
# "$PSScriptRoot\..\docs\reference" into "\..\docs\reference" -> C:\docs\reference.
if (-not $OutputDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutputDir = Join-Path $scriptDir '..\docs\reference'
}

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

    # --- Mount READ-ONLY, capturing progress encoding as we go ----------------------
    # Two jobs at once. The open question from docs/spikes/dism-backend.md section 5 is
    # whether DISM's progress bar survives stdout redirection, and only a LONG operation
    # renders one - an enumeration like /Get-WimInfo prints no bar at all, so probing that
    # would answer nothing. The mount is the natural long operation, so redirect it.

    # DISM refuses to mount into a non-empty directory (0xc1420114), and a previous failed
    # run leaves one behind. Clear it. Safe because the mounted-image check above already
    # confirmed nothing is registered against it.
    Write-Step "Preparing mount directory"
    & dism.exe /English /Cleanup-Mountpoints 2>&1 | Out-Null
    if (Test-Path $mountDir) { Remove-Item $mountDir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $mountDir | Out-Null
    $leftover = @(Get-ChildItem $mountDir -Force -ErrorAction SilentlyContinue).Count
    if ($leftover -gt 0) { Write-Error "Mount directory is not empty and could not be cleared: $mountDir"; exit 1 }
    Write-Ok "Empty mount directory ready."

    Write-Step "Mounting image index $Index (read-only, this takes a few minutes)"
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = "dism.exe"
    $psi.Arguments = "/English /Mount-Wim /WimFile:`"$wim`" /Index:$Index /MountDir:`"$mountDir`" /ReadOnly"
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    $raw = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()

    if ($p.ExitCode -ne 0) {
        Write-Host $raw
        Write-Error "Mount failed with exit code $($p.ExitCode)"
        exit 1
    }
    $mounted = $true
    Write-Ok "Mounted read-only at $mountDir"

    $bytes = [Text.Encoding]::UTF8.GetBytes($raw)
    $bs = ($bytes | Where-Object { $_ -eq 0x08 }).Count
    $cr = ($bytes | Where-Object { $_ -eq 0x0D }).Count
    $lf = ($bytes | Where-Object { $_ -eq 0x0A }).Count
    $pct = ([regex]::Matches($raw, '\d+(\.\d+)?%')).Count
    @"
DISM progress encoding under stdout redirection
===============================================
Captured from a real /Mount-Wim, which is long enough to render a progress bar.
(An enumeration such as /Get-WimInfo prints no bar, so probing it proves nothing.)

backspace (0x08) count : $bs
CR        (0x0D) count : $cr
LF        (0x0A) count : $lf
percent tokens matched : $pct

Interpretation:
  backspace > 0        -> bar is \b-driven; parseable incrementally
  CR >> LF             -> bar rewrites the line with bare \r; split on \r to read it
  percent tokens > 0   -> percentages ARE present in redirected output, whatever the
                          rewrite mechanism, so DismExeBackend can report real progress
  all ~0               -> bar suppressed off-console; only stage transitions available

--- raw capture follows ---
$raw
"@ | Set-Content "$OutputDir\02-progress-encoding.txt" -Encoding utf8
    Write-Ok "02-progress-encoding.txt  (backspaces=$bs cr=$cr lf=$lf percent-tokens=$pct)"

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

    # --- Offline registry hives -----------------------------------------------------
    # docs/catalog-gaps.md section 3.3: registry actions are the LEAST verified part of the
    # catalog. Everything else was verified with 7-Zip unelevated; the hives could not be,
    # because reg load needs admin. This section is the reason the script still matters.
    #
    # Uses reg.exe rather than in-process .NET on purpose: reg.exe exits after each call, so
    # it cannot pin a hive open the way a stray managed RegistryKey finalizer can. That is
    # exactly the hazard docs/PLAN.md section 3.3 is about, and this side-steps it entirely.

    Write-Step "Capturing offline registry hives"
    $hives = @(
        @{ Mount = 'TW_SOFTWARE'; Path = "$mountDir\Windows\System32\config\SOFTWARE" },
        @{ Mount = 'TW_SYSTEM';   Path = "$mountDir\Windows\System32\config\SYSTEM" },
        @{ Mount = 'TW_NTUSER';   Path = "$mountDir\Users\Default\ntuser.dat" }
    )
    $loaded = @()
    try {
        foreach ($h in $hives) {
            if (-not (Test-Path $h.Path)) { Write-Warn "Missing hive: $($h.Path)"; continue }
            & reg.exe load "HKLM\$($h.Mount)" $h.Path 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { $loaded += $h.Mount; Write-Ok "Loaded $($h.Mount)" }
            else { Write-Warn "Failed to load $($h.Mount) (exit $LASTEXITCODE)" }
        }

        # The TaskCache registration behind docs/catalog-gaps.md section 3.1 - this is where
        # scheduled tasks actually live in an offline image, not in Windows\System32\Tasks.
        if ($loaded -contains 'TW_SOFTWARE') {
            & reg.exe query "HKLM\TW_SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree" /s `
                > "$OutputDir\09-taskcache-tree.txt" 2>&1
            Write-Ok "09-taskcache-tree.txt"

            & reg.exe export "HKLM\TW_SOFTWARE\Policies" "$OutputDir\10-software-policies.reg" /y 2>&1 | Out-Null
            Write-Ok "10-software-policies.reg"
        }
        if ($loaded -contains 'TW_SYSTEM') {
            & reg.exe query "HKLM\TW_SYSTEM\ControlSet001\Services" > "$OutputDir\11-services.txt" 2>&1
            Write-Ok "11-services.txt"
        }
        if ($loaded -contains 'TW_NTUSER') {
            & reg.exe export "HKLM\TW_NTUSER\Software\Microsoft\Windows\CurrentVersion" `
                "$OutputDir\12-ntuser-currentversion.reg" /y 2>&1 | Out-Null
            Write-Ok "12-ntuser-currentversion.reg"
        }
    }
    finally {
        foreach ($m in $loaded) {
            & reg.exe unload "HKLM\$m" 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Ok "Unloaded $m" }
            else { Write-Warn "FAILED to unload $m - the image may not dismount. Check with: reg query HKLM\$m" }
        }
    }
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
