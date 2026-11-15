# targets.ps1 - Shared category definitions and helpers for OmniClean's
# PowerShell engine. Dot-sourced by scan.ps1 and cleaner.ps1 so both files
# use exactly the same target list (single source of truth).
#
# NOTE: The id values defined here MUST stay in sync with the CATEGORIES
# allowlist in server.js. test/smoke.test.js fails if the two lists drift.

$script:OmniCleanRecycleSid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
$script:OmniCleanActiveDrive = 'C:'

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
        id    = 'browser_cache'
        name  = 'Browser Caches & Shaders'
        desc  = 'Chrome, Edge, Brave, Firefox and Opera cache data'
        kind  = 'browser_cache'
    },
    @{
        id    = 'win_update'
        name  = 'Windows Update Downloads'
        desc  = 'Stale update payloads left after patching'
        kind  = 'paths'
        paths = @('C:\Windows\SoftwareDistribution\Download')
    },
    @{
        id    = 'crash_dumps'
        name  = 'Crash Dumps & Error Reports'
        desc  = 'User crash dumps and the Windows Error Reporting queue'
        kind  = 'paths'
        paths = @(
            "$env:LOCALAPPDATA\CrashDumps",
            "$env:ProgramData\Microsoft\Windows\WER\ReportQueue",
            "$env:ProgramData\Microsoft\Windows\WER\ReportArchive"
        )
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

# Normalizes "d", "D:", "D:\" to "D:" and anything unusable to the system drive.
function Get-OmniCleanNormalizeDrive([string]$drive) {
    if (-not $drive) { return 'C:' }
    $d = $drive.Trim().ToUpper().TrimEnd('\', '/')
    if ($d -notmatch '^[A-Z]:$') { return 'C:' }
    return $d
}

# Selects the drive that drive-scoped targets (the recycle bin) operate on.
function Set-OmniCleanActiveDrive([string]$drive) {
    $script:OmniCleanActiveDrive = Get-OmniCleanNormalizeDrive $drive
    return $script:OmniCleanActiveDrive
}

function Get-OmniCleanActiveDrive {
    $script:OmniCleanActiveDrive
}

# Recycle Bin root for the current user on the active drive.
function Get-OmniCleanRecyclePath {
    (Get-OmniCleanActiveDrive) + '\$Recycle.Bin\' + $script:OmniCleanRecycleSid
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
# The newest folder is always preserved.
function Get-OmniCleanCapCutOldVersions {
    $apps = Join-Path $env:LOCALAPPDATA 'CapCut\Apps'
    if (-not (Test-Path -LiteralPath $apps)) { return @() }
    $versions = @(Get-ChildItem -LiteralPath $apps -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(\.\d+)+' -and -not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) } |
        Sort-Object -Property @{ Expression = { Get-OmniCleanVersion $_.Name } })
    if ($versions.Count -le 1) { return @() }
    $latest = $versions[-1]
    @($versions | Where-Object { $_.FullName -ne $latest.FullName })
}

# Cache folders of the installed Chromium / Firefox / Opera browsers.
# Only cached media, shaders and code caches are listed - never profiles,
# cookies, bookmarks or saved passwords.
function Get-OmniCleanBrowserCachePaths {
    $paths = @()
    $localApp = $env:LOCALAPPDATA
    $appData = $env:APPDATA

    $chromiumRoots = @(
        (Join-Path $localApp 'Google\Chrome\User Data'),
        (Join-Path $localApp 'Microsoft\Edge\User Data'),
        (Join-Path $localApp 'BraveSoftware\Brave-Browser\User Data')
    )
    foreach ($root in $chromiumRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($profileDir in (Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
            $name = $profileDir.Name
            if ($name -eq 'Default' -or $name -like 'Profile *' -or $name -like 'Guest*') {
                foreach ($sub in @('Cache', 'Code Cache', 'GPUCache')) {
                    $paths += (Join-Path $profileDir.FullName $sub)
                }
            }
        }
    }

    # Firefox keeps its caches inside each profile folder.
    $firefoxRoot = Join-Path $localApp 'Mozilla\Firefox\Profiles'
    if (Test-Path -LiteralPath $firefoxRoot) {
        foreach ($profileDir in (Get-ChildItem -LiteralPath $firefoxRoot -Directory -ErrorAction SilentlyContinue)) {
            foreach ($sub in @('cache2', 'startupCache', 'jumpListCache')) {
                $paths += (Join-Path $profileDir.FullName $sub)
            }
        }
    }

    # Opera and Opera GX.
    $paths += (Join-Path $localApp 'Opera Software\Opera Stable\Cache')
    $paths += (Join-Path $localApp 'Opera Software\Opera GX Stable\Cache')
    $paths += (Join-Path $appData 'Opera Software\Opera Stable\GPUCache')
    $paths += (Join-Path $appData 'Opera Software\Opera GX Stable\GPUCache')

    @($paths | Where-Object { $_ } | Select-Object -Unique)
}

# Recursive size of a single directory in MB (0 when missing/unreadable).
# Reparse points (junctions / symlinks) are skipped so a link pointing back
# up the tree can never turn the walk into an infinite loop.
function Get-OmniCleanDirSizeMB([string]$path) {
    if (-not $path) { return 0 }
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $measure = Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { -not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) } |
        Measure-Object -Property Length -Sum
    if ($measure -and $measure.Sum) {
        return [math]::Round($measure.Sum / 1MB, 2)
    }
    return 0
}

# The concrete locations a target would touch. Used by the UI to show what
# a cleanup affects before anything is deleted.
function Get-OmniCleanTargetPaths($target) {
    if (-not $target) { return @() }
    switch ($target.kind) {
        'recycle' { return @((Get-OmniCleanRecyclePath)) }
        'capcut_old' { return @((Get-OmniCleanCapCutOldVersions | ForEach-Object { $_.FullName })) }
        'browser_cache' { return @(Get-OmniCleanBrowserCachePaths) }
        default { return @($target.paths) }
    }
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
        'browser_cache' {
            $total = 0
            foreach ($p in (Get-OmniCleanBrowserCachePaths)) {
                $total += Get-OmniCleanDirSizeMB $p
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

# Counts real failures without letting a single $null turn into "1 error".
function Count-OmniCleanErrors($e) {
    @($e | Where-Object { $_ }).Count
}

# Empties the recycle bin of the active drive only (never other volumes).
function Remove-OmniCleanRecycleBin {
    $root = (Get-OmniCleanActiveDrive) + '\'
    $flags = 0x00000001 -bor 0x00000002 -bor 0x00000004  # NOCONFIRMATION | NOPROGRESSUI | NOSOUND
    try {
        if (-not ('OmniClean.ShellBin' -as [type])) {
            Add-Type -Namespace 'OmniClean' -Name 'ShellBin' -MemberDefinition @'
[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
public static extern uint SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
'@
        }
        [void][OmniClean.ShellBin]::SHEmptyRecycleBin([IntPtr]::Zero, $root, $flags)
        return 0
    } catch {
        # Fall back to clearing the current user's recycle folder directly.
        return Remove-OmniCleanChildren (Get-OmniCleanRecyclePath)
    }
}

# Deletes everything inside a folder but keeps the folder itself.
# Returns the number of items that could not be removed.
function Remove-OmniCleanChildren([string]$root) {
    if (-not $root) { return 0 }
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
        $failed += Count-OmniCleanErrors $errs
    }
    return $failed
}

# Removes the contents of every path belonging to a target.
# Returns the number of items that could NOT be removed (in use / access denied).
function Remove-OmniCleanTarget($target) {
    $locked = 0

    switch ($target.kind) {
        'recycle' {
            $locked += Remove-OmniCleanRecycleBin
        }
        'capcut_old' {
            foreach ($old in (Get-OmniCleanCapCutOldVersions)) {
                $errs = $null
                Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction SilentlyContinue -ErrorVariable errs
                $locked += Count-OmniCleanErrors $errs
            }
        }
        'browser_cache' {
            foreach ($p in (Get-OmniCleanBrowserCachePaths)) {
                $locked += Remove-OmniCleanChildren $p
            }
        }
        default {
            foreach ($p in $target.paths) {
                $locked += Remove-OmniCleanChildren $p
            }
        }
    }

    return $locked
}
