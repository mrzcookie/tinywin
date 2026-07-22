<#
.SYNOPSIS
    Runs the TinyWin.Registry tests that need elevation.

.DESCRIPTION
    Everything above the P/Invoke seam in TinyWin.Registry is unit tested against a fake and runs
    in ordinary CI. This script covers the other half: enabling SeBackupPrivilege and
    SeRestorePrivilege on the process token, and a real RegLoadKey / RegUnLoadKey round trip.

    It does NOT need a mounted image or an ISO. The tests build a directory shaped like a mounted
    image and put a genuine hive file in it, produced with 'reg save' from a throwaway HKCU key.
    That exercises the same code path an image's SOFTWARE hive would, on any machine.

    Pass -MountPath to additionally run the all-five-hives test against a real mounted image.

.PARAMETER MountPath
    Optional. A directory where a Windows image is already mounted (the one you passed to
    Mount-WindowsImage). Enables the test that loads all five hives.

.PARAMETER Configuration
    Build configuration. Defaults to Debug.

.EXAMPLE
    .\scripts\verify-offline-registry.ps1

.EXAMPLE
    .\scripts\verify-offline-registry.ps1 -MountPath C:\scratch\mount
#>
[CmdletBinding()]
param(
    [string] $MountPath,
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) {
    Write-Error @'
This script must run elevated. RegLoadKey fails with ERROR_ACCESS_DENIED otherwise, which is the
exact failure these tests exist to rule out.

Start an administrator PowerShell and run it again.
'@
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests\TinyWin.Registry.Tests\TinyWin.Registry.Tests.csproj'

if (-not (Test-Path $project)) {
    Write-Error "Could not find $project. Run this from a checkout of the repository."
    exit 1
}

# Warn about leftovers before the run rather than after, so a pre-existing stranded hive is not
# mistaken for something this run created.
$stranded = @(Get-ChildItem 'HKLM:\' | Where-Object { $_.PSChildName -like 'zTW*' })
if ($stranded.Count -gt 0) {
    Write-Warning "Hives already loaded before this run: $($stranded.PSChildName -join ', ')"
    Write-Warning "Unload them with: reg unload HKLM\<name>"
}

$env:TINYWIN_ELEVATED_TESTS = '1'

if ($MountPath) {
    if (-not (Test-Path $MountPath)) {
        Write-Error "MountPath '$MountPath' does not exist."
        exit 1
    }

    $softwareHive = Join-Path $MountPath 'Windows\System32\config\SOFTWARE'
    if (-not (Test-Path $softwareHive)) {
        Write-Error "No SOFTWARE hive at '$softwareHive'. Is an image actually mounted there?"
        exit 1
    }

    $env:TINYWIN_MOUNT_PATH = $MountPath
    Write-Host "Also verifying against the image mounted at $MountPath" -ForegroundColor Cyan
}
else {
    Remove-Item Env:\TINYWIN_MOUNT_PATH -ErrorAction SilentlyContinue
    Write-Host 'No -MountPath given; the all-five-hives test will be skipped.' -ForegroundColor Yellow
}

Write-Host 'Running elevated registry tests...' -ForegroundColor Cyan

try {
    dotnet test $project --configuration $Configuration --filter 'Category=Elevated' --logger 'console;verbosity=normal'
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item Env:\TINYWIN_ELEVATED_TESTS -ErrorAction SilentlyContinue
    Remove-Item Env:\TINYWIN_MOUNT_PATH -ErrorAction SilentlyContinue
}

# The tests unload what they load, but a failure mid-test is exactly the scenario worth checking
# for afterwards — that is the whole subject of docs/PLAN.md section 3.3.
$leftovers = @(Get-ChildItem 'HKLM:\' | Where-Object { $_.PSChildName -like 'zTW*' })
if ($leftovers.Count -gt 0) {
    Write-Warning "Hives still loaded after the run: $($leftovers.PSChildName -join ', ')"
    foreach ($name in $leftovers.PSChildName) {
        Write-Warning "  reg unload HKLM\$name"
    }
}
else {
    Write-Host 'No hives left loaded.' -ForegroundColor Green
}

exit $exitCode
