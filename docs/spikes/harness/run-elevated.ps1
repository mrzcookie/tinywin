# TinyWin spike §3.2 — elevated verification driver.
#
# READ-ONLY. It enumerates provisioned appx packages and reads image state.
# It never calls DismRemoveProvisionedAppxPackage and never modifies this machine.
#
# Run from an ELEVATED PowerShell:
#     powershell -ExecutionPolicy Bypass -File <this file>

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $root 'DismSpike\bin\Release\net10.0\DismSpike.exe'
$out  = Join-Path $root 'results'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "elevated: $elevated"
if (-not $elevated) { Write-Host "NOT ELEVATED - aborting." -ForegroundColor Red; exit 1 }
if (-not (Test-Path $exe)) {
    Write-Host "building scratch project..." -ForegroundColor Cyan
    dotnet build (Join-Path $root 'DismSpike') -c Release -v q --nologo
}

Write-Host "`n[1/6] static layout + export binding (also works unelevated)..." -ForegroundColor Cyan
& $exe layout *>&1 | Tee-Object "$out\01-layout.txt"

Write-Host "`n[2/6] Microsoft.Dism live enumeration..." -ForegroundColor Cyan
& $exe managed *>&1 | Tee-Object "$out\02-managed.txt"

Write-Host "`n[3/6] raw P/Invoke: stride forensics + typed marshal..." -ForegroundColor Cyan
& $exe pinvoke --decode *>&1 | Tee-Object "$out\03-pinvoke.txt"

Write-Host "`n[4/6] benchmark: dism.exe vs native enumeration..." -ForegroundColor Cyan
& $exe bench *>&1 | Tee-Object "$out\04-bench.txt"
Copy-Item (Join-Path $root 'DismSpike\bin\Release\net10.0\dism-exe-getprovisioned.txt') `
          "$out\05-dism-exe-output.txt" -ErrorAction SilentlyContinue

Write-Host "`n[5/6] raw dism.exe progress stream, redirected (CheckHealth)..." -ForegroundColor Cyan
cmd /c "dism /Online /Cleanup-Image /CheckHealth /English > `"$out\06-checkhealth.raw`" 2>&1"

Write-Host "`n[6/6] raw dism.exe progress stream, redirected (ScanHealth, 1-5 min)..." -ForegroundColor Cyan
cmd /c "dism /Online /Cleanup-Image /ScanHealth /English > `"$out\07-scanhealth.raw`" 2>&1"

# How does the progress bar actually encode when stdout is a pipe, not a console?
$raw = [IO.File]::ReadAllBytes("$out\07-scanhealth.raw")
$bs = ($raw | Where-Object { $_ -eq 8 }).Count
$cr = ($raw | Where-Object { $_ -eq 13 }).Count
$lf = ($raw | Where-Object { $_ -eq 10 }).Count
"bytes=$($raw.Length) backspace(0x08)=$bs CR=$cr LF=$lf" | Tee-Object "$out\08-progress-encoding.txt"

Write-Host "`nDONE. Results in $out" -ForegroundColor Green
Write-Host "Key things to read off:" -ForegroundColor Yellow
Write-Host "  03-pinvoke.txt  -> 'detected record stride' must say 68 bytes, and MATCH"
Write-Host "  02 vs 03        -> package lists must be identical"
Write-Host "  04-bench.txt    -> median dism.exe vs median native enum-only"
Write-Host "  08-*.txt        -> backspace count >0 means the progress bar is \b-driven"
