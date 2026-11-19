# scan.ps1 - Produces a single JSON document describing disk usage,
# cleanable categories and basic system telemetry for the web UI.
# This script must ONLY write JSON to stdout (no logs, no stray output).
#
# The category list comes from targets.json (shared with the native
# edition) and is measured through the resolvers in targets.ps1.

param(
    # Optional drive letter (C:, D:, ...) the scan should report on.
    [string]$Drive = 'C:'
)

$ErrorActionPreference = "SilentlyContinue"
# The category names and icons are non-ASCII. Without this the console
# codepage mangles them on the way to stdout and the JSON the server parses
# would be corrupt.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot 'targets.ps1')

$activeDrive = Set-OmniCleanActiveDrive $Drive

# --- System drive metrics ---
$totalGB = 0; $freeGB = 0; $usedGB = 0; $pctFree = 0
$disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$activeDrive'"
if ($disk -and $disk.Size -gt 0) {
    $totalGB = [math]::Round($disk.Size / 1GB, 2)
    $freeGB = [math]::Round($disk.FreeSpace / 1GB, 2)
    $usedGB = [math]::Round(($disk.Size - $disk.FreeSpace) / 1GB, 2)
    $pctFree = [math]::Round(($disk.FreeSpace / $disk.Size) * 100, 1)
}

# --- Category sizes, grouped for the UI ---
$categoryList = @()
$groupList = @()
foreach ($group in (Get-OmniCleanTaxonomy).groups) {
    $groupTargets = @()
    foreach ($t in $group.targets) {
        $sizeMB = Get-OmniCleanTargetSizeMB $t
        $paths = @(Get-OmniCleanTargetPaths $t)
        $groupTargets += [PSCustomObject]@{
            id      = $t.id
            name    = $t.name
            desc    = $t.desc
            kind    = $t.kind
            sizeMB  = $sizeMB
            paths   = $paths
            detected = ($paths.Count -gt 0)
        }
    }
    $groupList += [PSCustomObject]@{
        id      = $group.id
        name    = $group.name
        targets = $groupTargets
    }
}

# Flat list for convenience (the UI groups them, but other consumers
# may just want the list).
foreach ($g in $groupList) {
    foreach ($t in $g.targets) {
        $categoryList += $t
    }
}

# --- Scheduled task state ---
$task = schtasks /query /tn "StorageOptimizerAutoClean" 2>&1 | Out-Null
$hasTask = ($LASTEXITCODE -eq 0)

# --- Lightweight system telemetry for the header pill ---
$cpuObj = Get-CimInstance Win32_Processor | Select-Object -First 1
$cpu = if ($cpuObj -and $cpuObj.Name) { $cpuObj.Name.Trim() } else { '' }
$ramObj = Get-CimInstance Win32_ComputerSystem
$ramGB = if ($ramObj -and $ramObj.TotalPhysicalMemory) {
    [math]::Round($ramObj.TotalPhysicalMemory / 1GB, 0)
} else { 0 }
$gpuObj = Get-CimInstance Win32_VideoController | Select-Object -First 1
$gpu = if ($gpuObj -and $gpuObj.Name) { $gpuObj.Name.Trim() } else { '' }
$gpuList = @(Get-CimInstance Win32_VideoController |
    ForEach-Object { if ($_.Name) { $_.Name.Trim() } } |
    Where-Object { $_ } | Select-Object -Unique)
$arch = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
$uptimeHours = [math]::Round(((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalHours, 1)

# --- Available local fixed drives (for the drive picker) ---
$drives = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
    $letter = $_.DeviceID
    # NOTE: these must NOT reuse $freeGB / $totalGB. A ForEach-Object
    # script block shares the caller's scope, so assigning to those names
    # here would overwrite the active drive's metrics.
    $driveFreeGB = [math]::Round($_.FreeSpace / 1GB, 2)
    $driveTotalGB = [math]::Round($_.Size / 1GB, 2)
    $label = if ($_.VolumeName) { "$letter ($($_.VolumeName))" } else { $letter }
    [PSCustomObject]@{
        id      = $letter
        label   = $label
        freeGB  = $driveFreeGB
        totalGB = $driveTotalGB
    }
})
if ($drives.Count -eq 0) {
    $drives = @([PSCustomObject]@{ id = $activeDrive; label = $activeDrive; freeGB = 0; totalGB = 0 })
}

[PSCustomObject]@{
    disk = [PSCustomObject]@{
        drive     = $activeDrive
        totalGB   = $totalGB
        freeGB    = $freeGB
        usedGB    = $usedGB
        percentFree = $pctFree
    }
    categories = $categoryList
    groups    = $groupList
    drives = $drives
    scheduledTask = [PSCustomObject]@{
        enabled = $hasTask
        name    = 'StorageOptimizerAutoClean'
    }
    system = [PSCustomObject]@{
        cpu     = $cpu
        ramGB   = $ramGB
        gpu     = $gpu
        gpus    = $gpuList
        arch    = $arch
        uptimeHours = $uptimeHours
    }
    isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} | ConvertTo-Json -Depth 6
