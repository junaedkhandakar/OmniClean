# targets.ps1 - Shared cleanup engine for OmniClean's web edition.
#
# The taxonomy lives in targets.json, which is the single source of truth
# shared with the native edition (OmniClean.Resolvers.cs embeds the same
# file). This script turns each target into concrete filesystem paths,
# measures it and removes it.
#
# Resolution happens in phases so that no two targets can ever own the
# same file:
#   1. explicit    - targets that declare their paths (kind "paths")
#   2. specific    - targets that inspect a known app family
#                    (editor_cache, old_app_versions)
#   3. browsers    - every installed browser profile
#   4. graphics    - every graphics cache of every vendor
#   5. patterns    - broad pattern based discovery (kind "glob")
# A candidate that overlaps a path another target already owns is
# skipped, so a folder is never emptied twice and never counted twice.
#
# Safety rules that apply to every target:
#   * Only known cache folders are ever touched.
#   * Browser profiles, cookies, bookmarks and saved passwords are never
#     read or deleted.
#   * Reparse points (junctions / symlinks) are skipped so a link pointing
#     back up the tree can never turn a walk into an infinite loop.
#   * Protected system locations are refused even if a taxonomy entry
#     points at them.

$script:OmniCleanRecycleSid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
$script:OmniCleanActiveDrive = 'C:'
$script:OmniCleanTaxonomy = $null
$script:OmniCleanResolved = @{}
$script:OmniCleanClaimed = New-Object System.Collections.Generic.List[string]
$script:OmniCleanResolutionReady = $false

# Folder names that hold graphics caches for ANY vendor. The list is based
# on the cache names the drivers use, not on the vendor, so a GPU OmniClean
# has never heard of is still detected.
$script:OmniCleanGpuCacheNames = @(
    'DXCache', 'DxcCache', 'GLCache', 'VkCache', 'VulkanCache',
    'D3DSCache', 'ShaderCache', 'GrShaderCache', 'ComputeCache',
    'PipelineCache', 'DawnCache', 'DawnGraphiteCache', 'DawnWebGPUCache',
    'shadercache', 'NV_Cache', 'PPCache', 'DisplayCache'
)

# Vendor folder names - used to look one level deeper for downloads and
# installer payloads that are not named like a shader cache.
$script:OmniCleanVendorTokens = @(
    'nvidia', 'amd', 'intel', 'qualcomm', 'via', 'mesa', 'broadcom',
    'imagination', 'arm', 'd3d', 'gpu', 'graphics', 'radeon', 'geforce', 'arc'
)

# Cache folders inside a Chromium / Electron profile.
$script:OmniCleanChromiumSubs = @(
    'Cache', 'Code Cache', 'GPUCache', 'DawnCache', 'DawnGraphiteCache',
    'DawnWebGPUCache', 'GrShaderCache', 'ShaderCache'
)

# ---- environment expansion -------------------------------------------------

# Expands %VAR% tokens against the current environment. Unknown tokens
# collapse to an empty string so a bad path is skipped instead of pointing
# at a literal "%FOO%" directory.
function Expand-OmniCleanEnv([string]$path) {
    if (-not $path) { return '' }
    $result = [regex]::Replace($path, '%([A-Za-z_][A-Za-z0-9_]*)%', {
        param($m)
        $value = [Environment]::GetEnvironmentVariable($m.Groups[1].Value)
        if ($value) { return $value } else { return '' }
    })
    if ($result -match '%') { return '' }
    return $result
}

# ---- taxonomy --------------------------------------------------------------

function Get-OmniCleanTaxonomy {
    if ($null -eq $script:OmniCleanTaxonomy) {
        $path = Join-Path $PSScriptRoot 'targets.json'
        # -Encoding UTF8 is required: without it PowerShell 5.1 reads the file
        # with the system ANSI codepage and mangles every non-ASCII
        # character (the category icons) into mojibake.
        $raw = Get-Content -LiteralPath $path -Raw -Encoding UTF8
        $script:OmniCleanTaxonomy = $raw | ConvertFrom-Json
    }
    return $script:OmniCleanTaxonomy
}

# Every target across every group, flattened.
# NOTE: the targets are emitted individually (no unary comma) so that
# "Get-OmniCleanTargets | Where-Object ..." filters per target. Wrapping the
# array in a comma would hand the pipeline ONE object - the whole array -
# and the filter would then match every target at once.
function Get-OmniCleanTargets {
    $out = @()
    foreach ($group in (Get-OmniCleanTaxonomy).groups) {
        foreach ($t in $group.targets) {
            $t | Add-Member -NotePropertyName 'group' -NotePropertyValue $group.name -Force
            $t | Add-Member -NotePropertyName 'groupId' -NotePropertyValue $group.id -Force
            $t | Add-Member -NotePropertyName 'groupIcon' -NotePropertyValue $group.icon -Force
            $out += $t
        }
    }
    $out
}

function Get-OmniCleanTargetIds {
    @(Get-OmniCleanTargets | ForEach-Object { $_.id })
}

function Get-OmniCleanTargetById([string]$id) {
    @(Get-OmniCleanTargets | Where-Object { $_.id -eq $id })[0]
}

# The cleanup mode of a target: "contents" (default) or "icons_only".
function Get-OmniCleanCleanupMode($target) {
    if ($target -and ($target.PSObject.Properties.Name -contains 'cleanup') -and $target.cleanup) {
        return [string]$target.cleanup
    }
    return 'contents'
}

# True when a target needs administrator rights to be cleaned.
function Test-OmniCleanNeedsAdmin($target) {
    return ($target -and ($target.PSObject.Properties.Name -contains 'requires_admin') -and [bool]$target.requires_admin)
}

# ---- drive handling --------------------------------------------------------

function Get-OmniCleanNormalizeDrive([string]$drive) {
    if (-not $drive) { return 'C:' }
    $d = $drive.Trim().ToUpper().TrimEnd('\', '/')
    if ($d -notmatch '^[A-Z]:$') { return 'C:' }
    return $d
}

function Set-OmniCleanActiveDrive([string]$drive) {
    $script:OmniCleanActiveDrive = Get-OmniCleanNormalizeDrive $drive
    $script:OmniCleanResolved = @{}
    $script:OmniCleanClaimed = New-Object System.Collections.Generic.List[string]
    $script:OmniCleanResolutionReady = $false
    return $script:OmniCleanActiveDrive
}

function Get-OmniCleanActiveDrive {
    $script:OmniCleanActiveDrive
}

function Get-OmniCleanRecyclePath {
    (Get-OmniCleanActiveDrive) + '\$Recycle.Bin\' + $script:OmniCleanRecycleSid
}

# ---- generic helpers -------------------------------------------------------

# Existence check that never throws. Test-Path raises a terminating
# "Access is denied" for folders the current user cannot enumerate, and
# that would abort the whole scan - the .NET API returns false instead.
function Test-OmniCleanExists([string]$path) {
    if (-not $path) { return $false }
    try { return [System.IO.File]::Exists($path) -or [System.IO.Directory]::Exists($path) } catch { return $false }
}

# Roots that are scanned for empty directories. These are the temp areas
# only - walking a whole volume would take minutes and is not what an
# "empty temporary directories" cleanup is about.
function Get-OmniCleanTempRoots {
    $roots = @()
    foreach ($p in @($env:TEMP, (Join-Path $env:WINDIR 'Temp'), (Join-Path $env:LOCALAPPDATA 'Temp'))) {
        if ($p -and (Test-OmniCleanExists $p)) { $roots += $p }
    }
    @($roots | Select-Object -Unique)
}

# Paths that must never be emptied, even if a taxonomy entry points at
# them. Deleting any of these breaks Windows or removes data that is not a
# cache.
$script:OmniCleanProtectedPaths = @(
    "$env:WINDIR\WinSxS",
    "$env:WINDIR\Installer",
    "$env:WINDIR\servicing",
    "$env:WINDIR\System32\DriverStore",
    "$env:WINDIR\System32\config",
    "$env:SystemRoot\System32\spool\drivers",
    "$env:ProgramData\Package Cache",
    "$env:LOCALAPPDATA\Package Cache",
    "$env:LOCALAPPDATA\Docker\wsl",
    "$env:LOCALAPPDATA\Docker\vfs",
    "$env:ProgramFiles",
    "${env:ProgramFiles(x86)}"
)

# True when the path is (or lives under) a protected location.
function Test-OmniCleanProtected([string]$path) {
    if (-not $path) { return $true }
    $full = $path
    try { $full = [System.IO.Path]::GetFullPath($path) } catch { return $true }
    foreach ($guard in $script:OmniCleanProtectedPaths) {
        if (-not $guard) { continue }
        $g = $guard.TrimEnd('\')
        if ($full.Equals($g, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
        if ($full.StartsWith($g + '\', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# Normalised form used for ownership comparisons.
function Get-OmniCleanNormalized([string]$path) {
    if (-not $path) { return '' }
    $full = $path
    try { $full = [System.IO.Path]::GetFullPath($path) } catch { return '' }
    return $full.TrimEnd('\').ToLowerInvariant()
}

# True when the path overlaps something that was already resolved: it is
# the same path, it lives inside an owned path, or it contains one.
function Test-OmniCleanOverlap([string]$path) {
    $norm = Get-OmniCleanNormalized $path
    if (-not $norm) { return $true }
    foreach ($claimed in $script:OmniCleanClaimed) {
        if ($claimed -eq $norm) { return $true }
        if ($norm.StartsWith($claimed + '\', [System.StringComparison]::Ordinal)) { return $true }
        if ($claimed.StartsWith($norm + '\', [System.StringComparison]::Ordinal)) { return $true }
    }
    return $false
}

# Accepts a candidate path into a target: it must exist, must not be
# protected and must not overlap a path another target already owns.
function Test-OmniCleanAcceptable([string]$path) {
    if (-not $path) { return $false }
    if (-not (Test-OmniCleanExists $path)) { return $false }
    if (Test-OmniCleanProtected $path) { return $false }
    if (Test-OmniCleanOverlap $path) { return $false }
    return $true
}

# Splits a path into its segments.
function Split-OmniCleanSegments([string]$path) {
    if (-not $path) { return @() }
    @((($path -split '[\\/]+') | Where-Object { $_ -ne '' }))
}

# Recursive size of a directory in MB.
#
# The walk is done by hand instead of Get-ChildItem -Recurse: PowerShell
# 5.1 follows junctions during a recursive enumeration, so a link pointing
# back up the tree would loop forever. Every directory entry is checked for
# the reparse point attribute before it is descended into.
function Get-OmniCleanDirSizeMB([string]$path) {
    if (-not $path) { return 0 }
    if (-not (Test-OmniCleanExists $path)) { return 0 }
    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    if ($item -and -not $item.PSIsContainer) {
        return [math]::Round(($item.Length / 1MB), 2)
    }

    [int64]$bytes = 0
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($path)
    while ($stack.Count -gt 0) {
        $current = $stack.Pop()
        $entries = $null
        try { $entries = [System.IO.Directory]::GetFileSystemEntries($current) } catch { $entries = $null }
        if ($null -eq $entries) { continue }
        foreach ($entry in $entries) {
            try {
                $attr = [System.IO.File]::GetAttributes($entry)
                if (($attr -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                if (($attr -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    $stack.Push($entry)
                } else {
                    $bytes += ([System.IO.FileInfo]$entry).Length
                }
            } catch { }
        }
    }
    return [math]::Round(($bytes / 1MB), 2)
}

# Counts real failures without letting a single $null turn into "1 error".
function Count-OmniCleanErrors($e) {
    @($e | Where-Object { $_ }).Count
}

# Parses the leading numeric version out of a folder name such as "2.9.1".
function Get-OmniCleanVersion([string]$name) {
    $m = [regex]::Match($name, '^\d+(\.\d+){0,3}')
    if ($m.Success) {
        try { return [version]$m.Value } catch { return [version]'0.0' }
    }
    return [version]'0.0'
}

# Thumbnail and icon cache database files. Only these are removed for a
# target with the "icons_only" cleanup mode - the surrounding Explorer
# folder holds much more than cache data.
function Test-OmniCleanIconCacheFile([string]$name) {
    if (-not $name) { return $false }
    return $name -match '^(thumbcache|iconcache).*\.db(-wal|-shm|-journal)?$'
}

# The icon cache files inside a folder (or the file itself).
function Get-OmniCleanIconCacheFiles([string]$folder) {
    $files = @()
    if (-not $folder -or -not (Test-OmniCleanExists $folder)) { return $files }
    $item = Get-Item -LiteralPath $folder -Force -ErrorAction SilentlyContinue
    if ($item -and -not $item.PSIsContainer) {
        if (Test-OmniCleanIconCacheFile $item.Name) { $files += $item.FullName }
        return $files
    }
    foreach ($f in (Get-ChildItem -LiteralPath $folder -File -Force -ErrorAction SilentlyContinue)) {
        if (Test-OmniCleanIconCacheFile $f.Name) { $files += $f.FullName }
    }
    $files
}

# ---- pattern resolution ----------------------------------------------------

# Enumerates the child directories of a folder. .NET enumeration is used
# instead of Get-ChildItem because the discovery walks touch thousands of
# folders and PowerShell object creation dominates the runtime there.
function Get-OmniCleanChildDirs([string]$dir) {
    $out = New-Object System.Collections.Generic.List[string]
    try {
        foreach ($child in [System.IO.Directory]::GetDirectories($dir)) {
            $out.Add($child)
        }
    } catch { }
    $out
}

function Test-OmniCleanReparse([string]$path) {
    try {
        $attr = [System.IO.File]::GetAttributes($path)
        return (($attr -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
    } catch { return $true }
}

# Resolves a pattern against a base folder.
#
# Segments:
#   *  - matches any text inside one path segment
#   ** - matches any number of segments
#
# $exclude holds names that must be skipped in the first segment, which
# lets a broad pattern leave the folders of a dedicated target alone.
function Resolve-OmniCleanPattern([string]$baseDir, [string[]]$segments, $exclude, [bool]$directoriesOnly = $true) {
    $results = New-Object System.Collections.Generic.List[string]
    if (-not $baseDir -or -not $segments -or $segments.Count -eq 0) { return @() }
    if (-not [System.IO.Directory]::Exists($baseDir)) { return @() }

    $excluded = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if ($exclude) {
        foreach ($name in $exclude) { [void]$excluded.Add([string]$name) }
    }

    $stack = New-Object System.Collections.Generic.Stack[object]
    $stack.Push(@{ Dir = $baseDir; Index = 0 })

    while ($stack.Count -gt 0) {
        $node = $stack.Pop()
        $dir = $node.Dir
        $index = $node.Index
        $segment = $segments[$index]

        if ($segment -eq '**') {
            # "**" matches zero segments ...
            if ($index + 1 -lt $segments.Count) {
                $stack.Push(@{ Dir = $dir; Index = $index + 1 })
            }
            # ... or one or more.
            foreach ($child in (Get-OmniCleanChildDirs $dir)) {
                if (Test-OmniCleanReparse $child) { continue }
                $stack.Push(@{ Dir = $child; Index = $index })
            }
            continue
        }

        $isLast = ($index -eq $segments.Count - 1)
        $entries = @()
        if ($isLast) {
            try {
                foreach ($e in [System.IO.Directory]::GetFileSystemEntries($dir)) { $entries += $e }
            } catch { $entries = @() }
        } else {
            $entries = @(Get-OmniCleanChildDirs $dir)
        }

        foreach ($entry in $entries) {
            $name = [System.IO.Path]::GetFileName($entry)
            if ($index -eq 0 -and $excluded.Contains($name)) { continue }
            if (-not ($name -like $segment)) { continue }
            if (Test-OmniCleanReparse $entry) { continue }
            if ($isLast) {
                if ($directoriesOnly) {
                    $attr = [System.IO.File]::GetAttributes($entry)
                    if (($attr -band [System.IO.FileAttributes]::Directory) -eq 0) { continue }
                }
                $results.Add($entry)
            } else {
                $stack.Push(@{ Dir = $entry; Index = $index + 1 })
            }
        }
    }

    @($results | Select-Object -Unique)
}

# Resolves an absolute pattern that may contain wildcards. The leading
# segments without a wildcard form the base folder.
function Resolve-OmniCleanAbsolutePattern([string]$pattern) {
    $segments = Split-OmniCleanSegments $pattern
    if ($segments.Count -eq 0) { return @() }

    $fixed = New-Object System.Collections.Generic.List[string]
    $index = 0
    while ($index -lt $segments.Count -and $segments[$index] -notlike '*[*?]*') {
        $fixed.Add($segments[$index])
        $index++
    }

    $base = ($fixed -join '\')
    if ($fixed.Count -eq 1 -and $fixed[0] -match '^[A-Za-z]:$') { $base = $fixed[0] + '\' }

    if ($index -ge $segments.Count) {
        if (Test-OmniCleanExists $base) { return @($base) }
        return @()
    }

    $rest = @()
    for ($i = $index; $i -lt $segments.Count; $i++) { $rest += $segments[$i] }
    Resolve-OmniCleanPattern $base $rest $null $false
}

# Finds every folder with one of the given names, up to $maxDepth levels
# below the root.
function Get-OmniCleanDirsByName([string]$root, [string[]]$names, [int]$maxDepth) {
    $found = New-Object System.Collections.Generic.List[string]
    if (-not $root -or -not [System.IO.Directory]::Exists($root)) { return @() }

    $nameSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($n in $names) { [void]$nameSet.Add([string]$n) }

    $stack = New-Object System.Collections.Generic.Stack[object]
    $stack.Push(@{ Dir = $root; Depth = 0 })

    while ($stack.Count -gt 0) {
        $node = $stack.Pop()
        $dir = $node.Dir
        $depth = $node.Depth
        foreach ($child in (Get-OmniCleanChildDirs $dir)) {
            $name = [System.IO.Path]::GetFileName($child)
            if ($nameSet.Contains($name)) { $found.Add($child) }
            if ($depth + 1 -lt $maxDepth) {
                if (Test-OmniCleanReparse $child) { continue }
                $stack.Push(@{ Dir = $child; Depth = $depth + 1 })
            }
        }
    }

    @($found | Select-Object -Unique)
}

# ---- path resolvers, one per target kind -----------------------------------

# "paths" targets: expand every declared path and keep the ones that exist.
function Resolve-OmniCleanStaticPaths($target) {
    $resolved = @()
    foreach ($p in $target.paths) {
        $expanded = Expand-OmniCleanEnv $p
        if (-not $expanded) { continue }
        # A protected location is never even reported as cleanable.
        if (Test-OmniCleanProtected $expanded) { continue }
        $resolved += Resolve-OmniCleanAbsolutePattern $expanded
    }
    @($resolved | Select-Object -Unique)
}

# "glob" targets: patterns relative to a base folder.
function Resolve-OmniCleanGlobPaths($target) {
    $resolved = @()
    foreach ($root in $target.roots) {
        $base = Expand-OmniCleanEnv $root.base
        if (-not $base) { continue }
        if (Test-OmniCleanProtected $base) { continue }
        $exclude = $null
        if (($root.PSObject.Properties.Name -contains 'exclude') -and $root.exclude) { $exclude = $root.exclude }
        foreach ($pattern in $root.patterns) {
            $segments = Split-OmniCleanSegments $pattern
            if ($segments.Count -eq 0) { continue }
            $resolved += Resolve-OmniCleanPattern $base $segments $exclude $true
        }
    }
    @($resolved | Select-Object -Unique)
}

# Browser cache folders: every Chromium / Gecko / Opera / Vivaldi / Arc /
# WebView2 browser that is actually installed, in every profile.
function Get-OmniCleanBrowserCachePaths {
    $paths = @()
    $subs = $script:OmniCleanChromiumSubs

    # Chromium family: every folder literally named "User Data" holds the
    # profiles of one browser. The name is what the browsers agree on, so
    # this finds browsers OmniClean has never heard of.
    $roots = @()
    foreach ($base in @($env:LOCALAPPDATA, $env:APPDATA)) {
        if (-not $base) { continue }
        $roots += Get-OmniCleanDirsByName $base @('User Data') 3
    }

    # Opera and Opera GX keep their caches one level deeper.
    $operaRoot = Join-Path $env:LOCALAPPDATA 'Opera Software'
    if (Test-OmniCleanExists $operaRoot) {
        foreach ($browser in (Get-OmniCleanChildDirs $operaRoot)) {
            $roots += $browser
        }
    }

    foreach ($root in ($roots | Select-Object -Unique)) {
        $profiles = @(Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile *' -or $_.Name -like 'Guest*' -or $_.Name -like 'System Profile*' })
        if ($profiles.Count -eq 0) {
            # Opera style: the cache folders sit directly in the root.
            foreach ($sub in $subs) {
                $p = Join-Path $root $sub
                if (Test-OmniCleanExists $p) { $paths += $p }
            }
            continue
        }
        foreach ($profile in $profiles) {
            foreach ($sub in $subs) {
                $p = Join-Path $profile.FullName $sub
                if (Test-OmniCleanExists $p) { $paths += $p }
            }
        }
    }

    # Firefox / Gecko keeps its caches inside each profile folder.
    $geckoRoots = @()
    foreach ($base in @($env:LOCALAPPDATA, $env:APPDATA)) {
        if (-not $base) { continue }
        $geckoRoots += Get-OmniCleanDirsByName $base @('Profiles') 4 |
            Where-Object { $_ -like '*\Mozilla\Firefox\Profiles' -or $_ -like '*\SeaMonkey\Profiles' }
    }
    foreach ($profileRoot in ($geckoRoots | Select-Object -Unique)) {
        foreach ($profile in (Get-ChildItem -LiteralPath $profileRoot -Directory -Force -ErrorAction SilentlyContinue)) {
            foreach ($sub in @('cache2', 'startupCache', 'jumpListCache', 'OfflineCache')) {
                $p = Join-Path $profile.FullName $sub
                if (Test-OmniCleanExists $p) { $paths += $p }
            }
        }
    }

    @($paths | Where-Object { $_ -and (Test-OmniCleanExists $_) } | Select-Object -Unique)
}

# Graphics caches of every vendor: NVIDIA, AMD, Intel, Qualcomm, VIA, Mesa
# and any vendor that is not on this list. Detection is based on the cache
# folder names the drivers use, so unknown vendors are found too.
function Get-OmniCleanGpuCachePaths {
    $paths = @()
    foreach ($base in @($env:LOCALAPPDATA, $env:ProgramData)) {
        if (-not $base) { continue }
        $paths += Get-OmniCleanDirsByName $base $script:OmniCleanGpuCacheNames 3
    }

    # Vendor folders may hold caches under a name that is not in the list
    # (for example "ota-artifacts"), so the well known vendor roots are
    # inspected one level deeper as well.
    foreach ($base in @($env:LOCALAPPDATA, $env:ProgramData)) {
        if (-not $base) { continue }
        foreach ($vendor in (Get-OmniCleanChildDirs $base)) {
            $name = [System.IO.Path]::GetFileName($vendor).ToLowerInvariant()
            $isVendor = $false
            foreach ($token in $script:OmniCleanVendorTokens) {
                if ($name.Contains($token)) { $isVendor = $true; break }
            }
            if (-not $isVendor) { continue }
            foreach ($child in (Get-OmniCleanChildDirs $vendor)) {
                $childName = [System.IO.Path]::GetFileName($child)
                if ($script:OmniCleanGpuCacheNames -contains $childName) {
                    $paths += $child
                } elseif ($childName -in @('Downloader', 'ota-artifacts', 'Download', 'PPCache', 'CN', 'DisplayCache', 'Caches')) {
                    $paths += $child
                }
            }
        }
    }

    @($paths | Where-Object { $_ -and (Test-OmniCleanExists $_) } | Select-Object -Unique)
}

# Caches of the installed code editors.
function Get-OmniCleanEditorCachePaths($target) {
    $paths = @()
    $apps = @()
    if (($target.PSObject.Properties.Name -contains 'apps') -and $target.apps) { $apps = $target.apps }
    $subs = @('Cache', 'Code Cache', 'GPUCache', 'CachedData', 'CachedExtensions', 'CachedExtensionVSIXs',
        'logs', 'Crashpad', 'DawnGraphiteCache', 'DawnWebGPUCache', 'Service Worker\CacheStorage')
    foreach ($app in $apps) {
        foreach ($base in @($env:APPDATA, $env:LOCALAPPDATA)) {
            if (-not $base) { continue }
            $root = Join-Path $base $app
            if (-not (Test-OmniCleanExists $root)) { continue }
            foreach ($sub in $subs) {
                $p = Join-Path $root $sub
                if (Test-OmniCleanExists $p) { $paths += $p }
            }
        }
    }
    @($paths | Where-Object { $_ -and (Test-OmniCleanExists $_) } | Select-Object -Unique)
}

# Outdated application builds. The newest build of every application is
# always kept.
function Get-OmniCleanOldAppVersionPaths($target) {
    $paths = @()
    $roots = @()
    if (($target.PSObject.Properties.Name -contains 'roots') -and $target.roots) { $roots = $target.roots }
    foreach ($raw in $roots) {
        $root = Expand-OmniCleanEnv $raw
        if (-not $root -or -not (Test-OmniCleanExists $root)) { continue }
        $versions = @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+(\.\d+)+' -and -not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) } |
            Sort-Object -Property @{ Expression = { Get-OmniCleanVersion $_.Name } })
        if ($versions.Count -le 1) { continue }
        $latest = $versions[-1]
        foreach ($v in $versions) {
            if ($v.FullName -ne $latest.FullName) { $paths += $v.FullName }
        }
    }
    @($paths | Select-Object -Unique)
}

# ---- resolution ------------------------------------------------------------

# Takes the candidate paths of one target and keeps the ones that may be
# touched. Everything that is kept becomes owned by this target.
function Accept-OmniCleanCandidates([string[]]$candidates) {
    $accepted = @()
    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        if (-not (Test-OmniCleanAcceptable $candidate)) { continue }
        $norm = Get-OmniCleanNormalized $candidate
        if ($norm) { $script:OmniCleanClaimed.Add($norm) }
        $accepted += $candidate
    }
    @($accepted | Select-Object -Unique)
}

# Concrete locations a target would touch.
function Get-OmniCleanTargetPaths($target) {
    if (-not $target) { return @() }

    # The whole taxonomy is resolved in one pass so that the ownership
    # rules are applied consistently no matter which target is asked for.
    Initialize-OmniCleanResolution

    if ($script:OmniCleanResolved.ContainsKey($target.id)) {
        return $script:OmniCleanResolved[$target.id]
    }
    return @()
}

# Resolves every target of the taxonomy in phase order.
function Initialize-OmniCleanResolution {
    if ($script:OmniCleanResolutionReady) { return }
    $script:OmniCleanResolutionReady = $true

    $targets = @(Get-OmniCleanTargets)

    # Phase 1: explicit paths. These are the most precise declarations, so
    # they get first claim on the folders they name.
    foreach ($t in $targets) {
        if ($t.kind -ne 'paths') { continue }
        $script:OmniCleanResolved[$t.id] = @(Accept-OmniCleanCandidates (Resolve-OmniCleanStaticPaths $t))
    }

    # Phase 2: specific app families.
    foreach ($t in $targets) {
        switch ($t.kind) {
            'editor_cache' { $script:OmniCleanResolved[$t.id] = @(Accept-OmniCleanCandidates (Get-OmniCleanEditorCachePaths $t)) }
            'old_app_versions' { $script:OmniCleanResolved[$t.id] = @(Accept-OmniCleanCandidates (Get-OmniCleanOldAppVersionPaths $t)) }
            default { }
        }
    }

    # Phase 3: browsers.
    foreach ($t in $targets) {
        if ($t.kind -ne 'browser_cache') { continue }
        $script:OmniCleanResolved[$t.id] = @(Accept-OmniCleanCandidates (Get-OmniCleanBrowserCachePaths))
    }

    # Phase 4: graphics caches of every vendor.
    foreach ($t in $targets) {
        if ($t.kind -ne 'gpu_cache') { continue }
        $script:OmniCleanResolved[$t.id] = @(Accept-OmniCleanCandidates (Get-OmniCleanGpuCachePaths))
    }

    # Phase 5: broad pattern based discovery.
    foreach ($t in $targets) {
        if ($t.kind -ne 'glob') { continue }
        $script:OmniCleanResolved[$t.id] = @(Accept-OmniCleanCandidates (Resolve-OmniCleanGlobPaths $t))
    }

    # Targets without a filesystem footprint.
    foreach ($t in $targets) {
        if ($script:OmniCleanResolved.ContainsKey($t.id)) { continue }
        switch ($t.kind) {
            'recycle' { $script:OmniCleanResolved[$t.id] = @((Get-OmniCleanRecyclePath)) }
            'empty_dirs' { $script:OmniCleanResolved[$t.id] = @(Get-OmniCleanTempRoots) }
            default { $script:OmniCleanResolved[$t.id] = @() }
        }
    }
}

# Total cleanable size of a target in MB.
function Get-OmniCleanTargetSizeMB($target) {
    $total = 0
    $mode = Get-OmniCleanCleanupMode $target
    foreach ($p in (Get-OmniCleanTargetPaths $target)) {
        if ($target.kind -eq 'empty_dirs') {
            $total += Get-OmniCleanEmptyDirCountMB $p
        } elseif ($mode -eq 'icons_only') {
            foreach ($f in (Get-OmniCleanIconCacheFiles $p)) {
                $fi = Get-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
                if ($fi) { $total += ($fi.Length / 1MB) }
            }
        } else {
            $total += Get-OmniCleanDirSizeMB $p
        }
    }
    return [math]::Round($total, 2)
}

# Empty directories are worth counting but weigh almost nothing, so the UI
# gets a small nominal size instead of a misleading zero.
function Get-OmniCleanEmptyDirCountMB([string]$root) {
    if (-not $root -or -not (Test-OmniCleanExists $root)) { return 0 }
    $count = 0
    try {
        $dirs = Get-ChildItem -LiteralPath $root -Recurse -Directory -Force -ErrorAction SilentlyContinue
        foreach ($d in $dirs) {
            if ($d.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
            if (@(Get-ChildItem -LiteralPath $d.FullName -Force -ErrorAction SilentlyContinue).Count -eq 0) {
                $count++
            }
        }
    } catch { }
    if ($count -gt 0) { return 0.5 }
    return 0
}

# ---- removal ---------------------------------------------------------------

# Deletes everything inside a folder but keeps the folder itself.
# Returns the number of items that could not be removed.
#
# The recursion is done by hand for the same reason the size walk is:
# Remove-Item -Recurse follows junctions on PowerShell 5.1 and can delete
# files through a link that points outside the cache.
function Remove-OmniCleanChildren([string]$root) {
    if (-not $root) { return 0 }
    if (-not (Test-OmniCleanExists $root)) { return 0 }
    if (Test-OmniCleanProtected $root) { return 0 }

    $failed = 0
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($root)

    while ($stack.Count -gt 0) {
        $current = $stack.Pop()
        $entries = $null
        try { $entries = [System.IO.Directory]::GetFileSystemEntries($current) } catch { $entries = $null }
        if ($null -eq $entries) { continue }

        foreach ($entry in $entries) {
            try {
                $attr = [System.IO.File]::GetAttributes($entry)
                $isDir = (($attr -band [System.IO.FileAttributes]::Directory) -ne 0)
                $isLink = (($attr -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)

                if ($isLink) {
                    # Delete the link itself, never its target.
                    if ($isDir) {
                        Remove-Item -LiteralPath $entry -Force -ErrorAction SilentlyContinue
                    } else {
                        Remove-Item -LiteralPath $entry -Force -ErrorAction SilentlyContinue
                    }
                    continue
                }

                if ($isDir) {
                    $stack.Push($entry)
                } else {
                    if (($attr -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                        try { [System.IO.File]::SetAttributes($entry, [System.IO.FileAttributes]::Normal) } catch { }
                    }
                    Remove-Item -LiteralPath $entry -Force -ErrorAction SilentlyContinue
                    if (Test-OmniCleanExists $entry) { $failed++ }
                }
            } catch { $failed++ }
        }
    }
    return $failed
}

# Removes a single file or a whole folder.
function Remove-OmniCleanPath([string]$path) {
    if (-not $path) { return 0 }
    if (-not (Test-OmniCleanExists $path)) { return 0 }
    if (Test-OmniCleanProtected $path) { return 0 }
    try {
        $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        if ($item -and $item.PSIsContainer) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            if ($item -and ($item.Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                try { $item.Attributes = [System.IO.FileAttributes]::Normal } catch { }
            }
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    } catch { return 1 }
    if (Test-OmniCleanExists $path) { return 1 }
    return 0
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

# Removes empty directories below a root.
function Remove-OmniCleanEmptyDirs([string]$root) {
    if (-not $root -or -not (Test-OmniCleanExists $root)) { return 0 }
    $failed = 0
    $dirs = @(Get-ChildItem -LiteralPath $root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } -Descending)
    foreach ($d in $dirs) {
        try {
            if ($d.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
            if (@(Get-ChildItem -LiteralPath $d.FullName -Force -ErrorAction SilentlyContinue).Count -eq 0) {
                Remove-Item -LiteralPath $d.FullName -Force -ErrorAction SilentlyContinue
            }
        } catch { $failed++ }
    }
    return $failed
}

# Removes the contents of every path belonging to a target.
# Returns the number of items that could NOT be removed (in use / access denied).
function Remove-OmniCleanTarget($target) {
    $locked = 0
    $mode = Get-OmniCleanCleanupMode $target

    switch ($target.kind) {
        'recycle' {
            $locked += Remove-OmniCleanRecycleBin
        }
        'empty_dirs' {
            foreach ($p in (Get-OmniCleanTempRoots)) {
                $locked += Remove-OmniCleanEmptyDirs $p
            }
        }
        'old_app_versions' {
            foreach ($p in (Get-OmniCleanTargetPaths $target)) {
                $locked += Remove-OmniCleanPath $p
            }
        }
        default {
            foreach ($p in (Get-OmniCleanTargetPaths $target)) {
                if ($mode -eq 'icons_only') {
                    foreach ($f in (Get-OmniCleanIconCacheFiles $p)) {
                        $locked += Remove-OmniCleanPath $f
                    }
                } elseif (Test-Path -LiteralPath $p -PathType Leaf) {
                    $locked += Remove-OmniCleanPath $p
                } else {
                    $locked += Remove-OmniCleanChildren $p
                }
            }
        }
    }

    return $locked
}
