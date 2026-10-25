# targets.ps1 - Shared category definitions and helpers for OmniClean's
# PowerShell engine. Dot-sourced by scan.ps1 and cleaner.ps1 so both files
# use exactly the same target list (single source of truth).
#
# NOTE: The id values defined here MUST stay in sync with the CATEGORIES
# allowlist in server.js. test/smoke.test.js fails if the two lists drift.

$script:OmniCleanRecycleSid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value

$script:OmniCleanTargets = @(
    @{
        id    = 'recycle_bin'
        name  = 'Recycle Bin'
        desc  = 'Deleted files and build caches'
        kind  = 'recycle'
    },
    @{
        id    = 'temp_files'
        name  = 'Temporary System & User Files'
        desc  = 'Windows and application temp files'
        kind  = 'paths'
        paths = @($env:TEMP, 'C:\Windows\Temp')
    },
    @{
        id    = 'nvidia_artifacts'
        name  = 'NVIDIA Driver Update Artifacts'
        desc  = 'Downloaded GPU driver packages and setup files'
        kind  = 'paths'
        paths = @('C:\ProgramData\NVIDIA Corporation\NVIDIA app\UpdateFramework\ota-artifacts')
    },
    @{
        id    = 'capcut_old'
        name  = 'Outdated CapCut Versions'
        desc  = 'Older CapCut builds leftover after updates'
        kind  = 'capcut_old'
    },
    @{
        id    = 'vscode_cache'
        name  = 'VS Code Caches & Crashpad Dumps'
        desc  = 'Cached extension installers and crash logs'
        kind  = 'paths'
        paths = @(
            "$env:APPDATA\Code\CachedExtensionVSIXs",
            "$env:APPDATA\Code\Crashpad\reports",
            "$env:APPDATA\Code\CachedData",
            "$env:APPDATA\Code\Cache"
        )
    },
    @{
        id    = 'dev_cache'
        name  = 'npm & pip Caches'
        desc  = 'Node and Python package download caches'
        kind  = 'paths'
        paths = @(
            "$env:LOCALAPPDATA\npm-cache",
            "$env:LOCALAPPDATA\pip\cache"
        )
    },
    @{
        id    = 'discord_cache'
        name  = 'Discord Cache'
        desc  = 'Discord media and shader caches'
        kind  = 'paths'
        paths = @("$env:APPDATA\discord\Cache")
    }
)

function Get-OmniCleanTargets {
    # Unary comma prevents the pipeline from unrolling the array.
    , $script:OmniCleanTargets
}

function Get-OmniCleanTargetIds {
    @($script:OmniCleanTargets | ForEach-Object { $_.id })
}

function Get-OmniCleanTargetById([string]$id) {
    $script:OmniCleanTargets | Where-Object { $_.id -eq $id } | Select-Object -First 1
}

# Recycle Bin root for the current user.
function Get-OmniCleanRecyclePath {
    'C:\$Recycle.Bin\' + $script:OmniCleanRecycleSid
}

# Parses the leading numeric version out of a folder name such as "2.9.1".
# Returns 0.0 when no version can be parsed (treated as oldest).
function Get-OmniCleanVersion([string]$name) {
    $m = [regex]::Match($name, '^\d+(\.\d+){0,3}')
    if ($m.Success) {
        try { return [version]$m.Value } catch { return [version]'0.0' }
    }
    return [version]'0.0'
}

# CapCut install folders whose version is older than the newest installed one.
function Get-OmniCleanCapCutOldVersions {
    $apps = Join-Path $env:USERPROFILE 'AppData\Local\CapCut\Apps'
    if (-not (Test-Path -LiteralPath $apps)) { return @() }
    $versions = @(Get-ChildItem -LiteralPath $apps -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(\.\d+)+' } |
        Sort-Object -Property @{ Expression = { Get-OmniCleanVersion $_.Name } })
    if ($versions.Count -le 1) { return @() }
    $latest = $versions[-1]
    @($versions | Where-Object { $_.FullName -ne $latest.FullName })
}

# Recursive size of a single directory in MB (0 when missing/unreadable).
function Get-OmniCleanDirSizeMB([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $measure = Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    if ($measure -and $measure.Sum) {
        return [math]::Round($measure.Sum / 1MB, 2)
    }
    return 0
}

# Total cleanable size of a target in MB (dispatches on target kind).
function Get-OmniCleanTargetSizeMB($target) {
    switch ($target.kind) {
        'recycle' {
            return Get-OmniCleanDirSizeMB (Get-OmniCleanRecyclePath)
        }
        'capcut_old' {
            $total = 0
            foreach ($old in (Get-OmniCleanCapCutOldVersions)) {
                $total += Get-OmniCleanDirSizeMB $old.FullName
            }
            return [math]::Round($total, 2)
        }
        default {
            $total = 0
            foreach ($p in $target.paths) {
                $total += Get-OmniCleanDirSizeMB $p
            }
            return [math]::Round($total, 2)
        }
    }
}

# Removes the contents of every path belonging to a target.
# Returns the number of items that could NOT be removed (in use / access denied).
function Remove-OmniCleanTarget($target) {
    $locked = 0

    function Count-Errors($e) {
        # @($null) would count as 1; filter empties so "0 errors" stays 0.
        @($e | Where-Object { $_ }).Count
    }

    function Remove-Children([string]$root) {
        if (-not (Test-Path -LiteralPath $root)) { return 0 }
        $failed = 0
        Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                if ($_.Attributes -band [System.IO.FileAttributes]::ReadOnly) {
                    $_.Attributes = [System.IO.FileAttributes]::Normal
                }
            } catch {}
            $errs = $null
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue -ErrorVariable errs
            $failed += Count-Errors $errs
        }
        return $failed
    }

    switch ($target.kind) {
        'recycle' {
            try {
                Clear-RecycleBin -Force -ErrorAction SilentlyContinue
            } catch {
                $locked += Remove-Children (Get-OmniCleanRecyclePath)
            }
        }
        'capcut_old' {
            foreach ($old in (Get-OmniCleanCapCutOldVersions)) {
                $errs = $null
                Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction SilentlyContinue -ErrorVariable errs
                $locked += Count-Errors $errs
            }
        }
        default {
            foreach ($p in $target.paths) {
                $locked += Remove-Children $p
            }
        }
    }

    return $locked
}
