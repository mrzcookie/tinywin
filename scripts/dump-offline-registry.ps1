<#
.SYNOPSIS
    Reads the offline hives of a mounted Windows image, for catalog verification.

.DESCRIPTION
    docs/catalog-gaps.md section 3.3 records that registry policy values are the least verified
    part of the catalog: the hives cannot be loaded without elevation, so every registry action is
    sourced from Microsoft's GPO/CSP documentation and from tiny11builder rather than from real
    media. This script is the elevated pass that settles them.

    Three modes, combinable:

      -VerifyCatalog   For every SetRegistry / DeleteRegistryKey action in the catalog, report
                       whether the key exists in the image, whether the value exists, and how the
                       image's current data compares with what the action would write. This is the
                       one that answers section 3.3.

      -TaskCache       Dump the scheduled task registrations under
                       SOFTWARE\...\Schedule\TaskCache, which is where a task actually lives on an
                       offline image (docs/catalog-gaps.md section 3.1). Confirms both the subkey
                       layout TinyWin.Registry assumes and which of the catalog's task paths are
                       genuinely registered on this media.

      -Key             Dump an arbitrary subtree of one hive.

    Hives are loaded read-only under a 'zTWDump-' prefix, deliberately distinct from the engine's
    own 'zTW-' prefix so this script and a concurrent build cannot unload each other's mounts.
    Everything it loads is unloaded in a finally block, and anything left over is reported loudly.

.PARAMETER MountPath
    Directory where the image is mounted (what you passed to Mount-WindowsImage).

.PARAMETER VerifyCatalog
    Check every catalog registry action against the image.

.PARAMETER CatalogPath
    Catalog directory. Defaults to the repository's catalog/ folder.

.PARAMETER TaskCache
    Dump TaskCache Tree/Tasks registrations.

.PARAMETER Hive
    Hive to dump with -Key. One of Components, Default, NtUser, Software, System.

.PARAMETER Key
    Hive-relative key path to dump recursively, e.g. 'Policies\Microsoft\Windows'.

.PARAMETER OutFile
    Optional CSV path for the -VerifyCatalog results.

.EXAMPLE
    .\scripts\dump-offline-registry.ps1 -MountPath C:\scratch\mount -VerifyCatalog -OutFile registry-audit.csv

.EXAMPLE
    .\scripts\dump-offline-registry.ps1 -MountPath C:\scratch\mount -TaskCache

.EXAMPLE
    .\scripts\dump-offline-registry.ps1 -MountPath C:\scratch\mount -Hive Software -Key 'Microsoft\Windows\CurrentVersion\Policies'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MountPath,

    [switch] $VerifyCatalog,
    [string] $CatalogPath,
    [switch] $TaskCache,

    [ValidateSet('Components', 'Default', 'NtUser', 'Software', 'System')]
    [string] $Hive,

    [string] $Key,
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'

$MountPrefix = 'zTWDump-'

# Must match HiveLayout.RelativeFilePath in src/TinyWin.Registry/HiveLayout.cs.
$HiveFiles = @{
    Components = 'Windows\System32\config\COMPONENTS'
    Default    = 'Windows\System32\config\default'
    NtUser     = 'Users\Default\ntuser.dat'
    Software   = 'Windows\System32\config\SOFTWARE'
    System     = 'Windows\System32\config\SYSTEM'
}

$TaskCacheRoot = 'Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache'

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-MountName {
    param([string] $HiveName)
    return "$MountPrefix$($HiveName.ToUpperInvariant())"
}

function Mount-OfflineHive {
    param([string] $HiveName)

    $file = Join-Path $MountPath $HiveFiles[$HiveName]
    if (-not (Test-Path $file)) {
        throw "No $HiveName hive at '$file'. Is an image actually mounted at '$MountPath'?"
    }

    $mount = Get-MountName $HiveName
    & reg.exe load "HKLM\$mount" $file | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "reg load HKLM\$mount failed with exit code $LASTEXITCODE."
    }

    Write-Verbose "Loaded $HiveName as HKLM\$mount"
    return $mount
}

function Dismount-OfflineHive {
    param([string] $MountName)

    # Same forced-finalization dance as HiveUnloader: PowerShell's own registry provider leaves
    # keys awaiting finalization, and they pin the hive exactly as .NET's RegistryKey does.
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        & reg.exe unload "HKLM\$MountName" 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Verbose "Unloaded HKLM\$MountName"
            return $true
        }

        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        Start-Sleep -Milliseconds (100 * $attempt)
    }

    Write-Warning "Could not unload HKLM\$MountName. Unload it manually before dismounting the image."
    return $false
}

function Get-ValueSnapshot {
    param([string] $FullKeyPath, [string] $ValueName)

    $result = [ordered]@{ KeyExists = $false; ValueExists = $false; Kind = ''; Data = '' }

    $key = Get-Item -LiteralPath "Registry::$FullKeyPath" -ErrorAction SilentlyContinue
    if ($null -eq $key) { return $result }
    $result.KeyExists = $true

    $name = $ValueName
    if ([string]::IsNullOrEmpty($name)) { $name = '(default)' }

    $names = $key.GetValueNames()
    $match = $names | Where-Object { $_ -eq $ValueName -or ([string]::IsNullOrEmpty($_) -and [string]::IsNullOrEmpty($ValueName)) }
    if ($null -eq $match) { return $result }

    $result.ValueExists = $true
    $result.Kind = $key.GetValueKind($ValueName)
    $raw = $key.GetValue($ValueName, $null, 'DoNotExpandEnvironmentNames')

    if ($raw -is [byte[]])       { $result.Data = ($raw | ForEach-Object { $_.ToString('x2') }) -join '' }
    elseif ($raw -is [string[]]) { $result.Data = $raw -join '|' }
    else                         { $result.Data = "$raw" }

    return $result
}

function Show-Subtree {
    param([string] $FullKeyPath, [int] $Depth = 0, [int] $MaxDepth = 8)

    $key = Get-Item -LiteralPath "Registry::$FullKeyPath" -ErrorAction SilentlyContinue
    if ($null -eq $key) {
        Write-Host "  (absent) $FullKeyPath" -ForegroundColor DarkGray
        return
    }

    $indent = '  ' * $Depth
    Write-Host "$indent[$($key.PSChildName)]" -ForegroundColor Cyan

    foreach ($valueName in $key.GetValueNames()) {
        $shown = $valueName
        if ([string]::IsNullOrEmpty($shown)) { $shown = '(default)' }
        $snapshot = Get-ValueSnapshot -FullKeyPath $FullKeyPath -ValueName $valueName
        Write-Host "$indent  $shown = $($snapshot.Data)  [$($snapshot.Kind)]"
    }

    if ($Depth -ge $MaxDepth) {
        Write-Host "$indent  ... (depth limit)" -ForegroundColor DarkGray
        return
    }

    foreach ($child in $key.GetSubKeyNames()) {
        Show-Subtree -FullKeyPath "$FullKeyPath\$child" -Depth ($Depth + 1) -MaxDepth $MaxDepth
    }
}

# --- preconditions ------------------------------------------------------------------------------

if (-not (Test-Elevated)) {
    Write-Error 'This script must run elevated: reg load needs SeBackupPrivilege/SeRestorePrivilege.'
    exit 1
}

if (-not (Test-Path $MountPath)) {
    Write-Error "MountPath '$MountPath' does not exist."
    exit 1
}

if (-not $VerifyCatalog -and -not $TaskCache -and -not $Key) {
    Write-Error 'Nothing to do. Pass -VerifyCatalog, -TaskCache, or -Hive/-Key.'
    exit 1
}

if ($Key -and -not $Hive) {
    Write-Error '-Key requires -Hive.'
    exit 1
}

if (-not $CatalogPath) {
    $CatalogPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'catalog'
}

$loaded = @()
$failedUnloads = @()

try {
    # ---------------------------------------------------------------------------------------------
    # -VerifyCatalog
    # ---------------------------------------------------------------------------------------------
    if ($VerifyCatalog) {
        if (-not (Test-Path $CatalogPath)) {
            throw "Catalog directory '$CatalogPath' not found. Pass -CatalogPath."
        }

        $actions = @()
        foreach ($file in Get-ChildItem -Path $CatalogPath -Filter '*.json') {
            $doc = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if ($null -eq $doc.components) { continue }

            foreach ($component in $doc.components) {
                foreach ($action in $component.actions) {
                    if ($action.type -eq 'SetRegistry' -or $action.type -eq 'DeleteRegistryKey') {
                        $actions += [pscustomobject]@{
                            Component = $component.id
                            Type      = $action.type
                            Hive      = $action.hive
                            Key       = $action.key
                            ValueName = $action.valueName
                            Expected  = $action.data
                        }
                    }
                }
            }
        }

        Write-Host "Checking $($actions.Count) registry action(s) from $CatalogPath" -ForegroundColor Cyan

        $neededHives = $actions.Hive | Sort-Object -Unique | Where-Object { $_ }
        $mounts = @{}
        foreach ($hiveName in $neededHives) {
            $mounts[$hiveName] = Mount-OfflineHive $hiveName
            $loaded += $mounts[$hiveName]
        }

        $results = foreach ($action in $actions) {
            $full = "HKEY_LOCAL_MACHINE\$($mounts[$action.Hive])\$($action.Key)"
            $snapshot = Get-ValueSnapshot -FullKeyPath $full -ValueName $action.ValueName

            $verdict = 'KeyMissing'
            if ($snapshot.KeyExists) {
                if ($action.Type -eq 'DeleteRegistryKey') {
                    $verdict = 'KeyPresent'
                }
                elseif (-not $snapshot.ValueExists) {
                    $verdict = 'ValueMissing'
                }
                elseif ("$($snapshot.Data)" -eq "$($action.Expected)") {
                    $verdict = 'AlreadyMatches'
                }
                else {
                    $verdict = 'ValueDiffers'
                }
            }

            [pscustomobject]@{
                Component = $action.Component
                Type      = $action.Type
                Hive      = $action.Hive
                Key       = $action.Key
                ValueName = $action.ValueName
                Expected  = "$($action.Expected)"
                Actual    = $snapshot.Data
                Kind      = "$($snapshot.Kind)"
                Verdict   = $verdict
            }
        }

        $results | Format-Table Component, Hive, Key, ValueName, Verdict, Actual -AutoSize | Out-String -Width 400 | Write-Host

        Write-Host ''
        Write-Host 'Summary:' -ForegroundColor Cyan
        $results | Group-Object Verdict | Sort-Object Count -Descending |
            ForEach-Object { Write-Host ("  {0,-16} {1}" -f $_.Name, $_.Count) }

        Write-Host ''
        Write-Host @'
How to read this:
  KeyMissing      The key does not exist on stock media. Normal for a policy key a SetRegistry
                  action is meant to create; a red flag for a DeleteRegistryKey action, which
                  will report NoTarget.
  ValueMissing    The key exists but the value does not. Normal for SetRegistry.
  AlreadyMatches  The image already has this value. The action is a genuine no-op.
  ValueDiffers    The image has a different value. This is the action doing real work.
  KeyPresent      A DeleteRegistryKey action has something to delete.
'@ -ForegroundColor DarkGray

        if ($OutFile) {
            $results | Export-Csv -LiteralPath $OutFile -NoTypeInformation -Encoding utf8
            Write-Host "Wrote $OutFile" -ForegroundColor Green
        }
    }

    # ---------------------------------------------------------------------------------------------
    # -TaskCache
    # ---------------------------------------------------------------------------------------------
    if ($TaskCache) {
        $mount = $null
        if ($loaded -contains (Get-MountName 'Software')) {
            $mount = Get-MountName 'Software'
        }
        else {
            $mount = Mount-OfflineHive 'Software'
            $loaded += $mount
        }

        $root = "HKEY_LOCAL_MACHINE\$mount\$TaskCacheRoot"
        $rootKey = Get-Item -LiteralPath "Registry::$root" -ErrorAction SilentlyContinue

        if ($null -eq $rootKey) {
            Write-Warning "No TaskCache at $root. TinyWin.Registry's scheduled task removal assumes it exists."
        }
        else {
            Write-Host "TaskCache subkeys present: $($rootKey.GetSubKeyNames() -join ', ')" -ForegroundColor Cyan
            Write-Host '(TinyWin.Registry assumes Tree plus Tasks/Plain/Logon/Boot/Maintenance.)' -ForegroundColor DarkGray
            Write-Host ''

            $registrations = New-Object System.Collections.Generic.List[object]

            function Walk-Tree {
                param([string] $Path, [string] $Display)

                $node = Get-Item -LiteralPath "Registry::$Path" -ErrorAction SilentlyContinue
                if ($null -eq $node) { return }

                $id = $node.GetValue('Id', $null)
                if ($id) {
                    $present = @()
                    foreach ($sub in @('Tasks', 'Plain', 'Logon', 'Boot', 'Maintenance')) {
                        $probe = "HKEY_LOCAL_MACHINE\$mount\$TaskCacheRoot\$sub\$id"
                        if (Test-Path -LiteralPath "Registry::$probe") { $present += $sub }
                    }

                    $registrations.Add([pscustomobject]@{
                        Task     = $Display
                        Id       = $id
                        IdKeyedIn = ($present -join ',')
                    })
                }

                foreach ($child in $node.GetSubKeyNames()) {
                    Walk-Tree -Path "$Path\$child" -Display "$Display\$child"
                }
            }

            Walk-Tree -Path "$root\Tree" -Display ''

            Write-Host "$($registrations.Count) task registration(s) found in the offline SOFTWARE hive." -ForegroundColor Cyan
            $registrations | Sort-Object Task | Format-Table -AutoSize | Out-String -Width 400 | Write-Host

            # Cross-check the catalog's task paths against what is actually registered.
            if (Test-Path $CatalogPath) {
                $catalogTasks = @()
                foreach ($file in Get-ChildItem -Path $CatalogPath -Filter '*.json') {
                    $doc = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
                    if ($null -eq $doc.components) { continue }
                    foreach ($component in $doc.components) {
                        foreach ($action in $component.actions) {
                            if ($action.type -eq 'RemoveScheduledTask') {
                                $catalogTasks += [pscustomobject]@{ Component = $component.id; Name = $action.name }
                            }
                        }
                    }
                }

                Write-Host ''
                Write-Host 'Catalog task paths against this image:' -ForegroundColor Cyan
                foreach ($task in $catalogTasks) {
                    $normalized = '\' + ($task.Name -replace '^[\\/]+', '' -replace '/', '\')
                    $hit = $registrations | Where-Object { $_.Task -eq $normalized }
                    if ($hit) {
                        Write-Host ("  REGISTERED   {0}  ({1})" -f $task.Name, $task.Component) -ForegroundColor Green
                    }
                    else {
                        Write-Host ("  NOT PRESENT  {0}  ({1})" -f $task.Name, $task.Component) -ForegroundColor Yellow
                    }
                }
            }

            if ($OutFile) {
                $taskOut = [IO.Path]::ChangeExtension($OutFile, $null) + '-taskcache.csv'
                $registrations | Export-Csv -LiteralPath $taskOut -NoTypeInformation -Encoding utf8
                Write-Host "Wrote $taskOut" -ForegroundColor Green
            }
        }
    }

    # ---------------------------------------------------------------------------------------------
    # -Hive/-Key
    # ---------------------------------------------------------------------------------------------
    if ($Key) {
        $mount = Get-MountName $Hive
        if ($loaded -notcontains $mount) {
            $mount = Mount-OfflineHive $Hive
            $loaded += $mount
        }

        Show-Subtree -FullKeyPath "HKEY_LOCAL_MACHINE\$mount\$Key"
    }
}
finally {
    # PowerShell caches provider keys aggressively, so drop everything before unloading.
    Remove-Variable -Name rootKey, node, key -ErrorAction SilentlyContinue

    foreach ($mount in ($loaded | Sort-Object -Unique)) {
        if (-not (Dismount-OfflineHive $mount)) {
            $failedUnloads += $mount
        }
    }
}

if ($failedUnloads.Count -gt 0) {
    Write-Error "Hives still loaded: $($failedUnloads -join ', '). Unload them before dismounting the image."
    exit 1
}

Write-Host 'Done; all hives unloaded.' -ForegroundColor Green
