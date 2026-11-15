# cleaner.ps1 - Storage Optimizer cleanup engine.
# Can be run silently on schedule, called on demand by server.js, or
# executed manually with -DryRun to preview what would be removed.
#
# IMPORTANT: -Categories receives a single comma-separated string.
# powershell.exe -File binds each CLI argument as a scalar, so a [string[]]
# parameter would arrive as ONE element ("a,b,c") and every -contains check
# would fail. Splitting here is what makes the clean action actually work.

param(
    [string]$Categories = "recycle_bin,temp_files,browser_cache,win_update,crash_dumps,nvidia_artifacts,capcut_old,vscode_cache,dev_cache,discord_cache",
    [switch]$DryRun,
    # Drive the drive-scoped targets (recycle bin) operate on.
    [string]$Drive = 'C:'
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'targets.ps1')

function Log([string]$msg) {
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Write-Output "[$timestamp] $msg"
}

$activeDrive = Set-OmniCleanActiveDrive $Drive

# --- Parse and validate requested categories (defense in depth:
# server.js also allowlists these before the script is ever spawned) ---
$requested = @($Categories -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$known = Get-OmniCleanTargetIds

if ($requested.Count -eq 0) {
    Log "[ERROR] No categories specified."
    exit 2
}
$unknown = @($requested | Where-Object { $known -notcontains $_ })
if ($unknown.Count -gt 0) {
    Log "[ERROR] Unknown category id(s): $($unknown -join ', ')"
    exit 2
}

$mode = if ($DryRun) { "DRY RUN (no files will be deleted)" } else { "LIVE CLEAN" }
Log "Starting Storage Optimization [$mode] on $activeDrive..."
Log "Categories requested: $($requested -join ', ')"

$beforeFree = $null
try {
    $beforeFree = (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$activeDrive'").FreeSpace
} catch { $beforeFree = $null }

$processed = 0
$failed = 0

foreach ($id in $requested) {
    $target = Get-OmniCleanTargetById $id
    if (-not $target) {
        $failed++
        Log "[FAILED] Unknown category: $id"
        continue
    }

    try {
        $sizeMB = Get-OmniCleanTargetSizeMB $target

        if ($DryRun) {
            $processed++
            Log "[DRY RUN] $($target.name): would clean up to $sizeMB MB"
            foreach ($p in (Get-OmniCleanTargetPaths $target)) {
                Log "[DRY RUN]   -> $p"
            }
            continue
        }

        if ($sizeMB -le 0) {
            $processed++
            Log "[OK] $($target.name): already clean"
            continue
        }

        $locked = Remove-OmniCleanTarget $target
        $processed++
        if ($locked -gt 0) {
            Log "[PARTIAL] $($target.name): processed ${sizeMB} MB, $locked item(s) in use were skipped"
        } else {
            Log "[OK] $($target.name): processed ${sizeMB} MB"
        }
    } catch {
        $failed++
        Log "[FAILED] $($target.name): $($_.Exception.Message)"
    }
}

if ($DryRun) {
    Log "Dry run finished: $processed category(ies) inspected, nothing was deleted."
    exit 0
}

$freedMB = 0
$currentFreeGB = 0
try {
    $afterFree = (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$activeDrive'").FreeSpace
    if ($beforeFree -ne $null -and $afterFree -ne $null) {
        $freedMB = [math]::Round([math]::Max(0, ($afterFree - $beforeFree)) / 1MB, 2)
    }
    $currentFreeGB = [math]::Round($afterFree / 1GB, 2)
} catch { }
Log "Optimization finished: $processed/$($requested.Count) categories processed, freed $freedMB MB. Current ${activeDrive} free space: $currentFreeGB GB."

if ($failed -gt 0) {
    Log "[ERROR] $failed category(ies) failed."
    exit 1
}
exit 0
