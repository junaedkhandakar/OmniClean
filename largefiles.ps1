# largefiles.ps1 - Deep scan for large files and space-hogging folders.
#
# Walks an entire volume (not just the user profile), skipping reparse
# points, and reports the biggest files plus the folders that hold them.
# Results are written as a single JSON document to stdout.
#
# Usage:
#   largefiles.ps1 -Drive C: -MinSizeMB 100 -Top 200

param(
    [string]$Drive = 'C:',
    [int]$MinSizeMB = 100,
    [int]$Top = 200,
    # Skip these system folders - they are huge but not user data and
    # deleting anything in them breaks Windows.
    [switch]$IncludeSystem
)

$ErrorActionPreference = "SilentlyContinue"

$root = $Drive.ToUpper().TrimEnd('\')
if ($root -notmatch '^[A-Z]:$') { $root = 'C:' }
$scanRoot = $root + '\'

$minBytes = [int64]$MinSizeMB * 1MB
$files = New-Object System.Collections.Generic.List[object]

# Folders that are reported separately: they are usually system data and
# deleting them is not a normal cleanup action.
$systemPrefixes = @(
    "$root\Windows\WinSxS",
    "$root\Windows\Installer",
    "$root\Windows\servicing",
    "$root\System Volume Information",
    "$root\$Recycle.Bin"
)

function Test-IsSystemPath([string]$path) {
    foreach ($p in $systemPrefixes) {
        if ($path.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# Iterative walk so a deep tree cannot overflow the stack, with reparse
# points skipped so a junction back up the tree cannot loop forever.
$stack = New-Object System.Collections.Generic.Stack[string]
$stack.Push($scanRoot)
$scannedFiles = 0
$scannedDirs = 0
$skipped = 0

while ($stack.Count -gt 0) {
    $current = $stack.Pop()
    $scannedDirs++

    $entries = $null
    try { $entries = [System.IO.Directory]::GetFileSystemEntries($current) } catch { $entries = $null }

    if ($null -ne $entries) {
        foreach ($entry in $entries) {
            try {
                $attr = [System.IO.File]::GetAttributes($entry)

                # Never follow a junction or symlink: it can point back up
                # the tree and the walk would never end.
                if (($attr -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $skipped++
                    continue
                }

                if (($attr -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    $stack.Push($entry)
                    continue
                }

                $scannedFiles++
                $info = New-Object System.IO.FileInfo($entry)
                if ($info.Length -lt $minBytes) { continue }

                $isSystem = Test-IsSystemPath $entry
                if ($isSystem -and -not $IncludeSystem) { continue }

                $ext = $info.Extension
                if ([string]::IsNullOrEmpty($ext)) { $ext = '' } else { $ext = $ext.TrimStart('.').ToUpper() }

                $files.Add([PSCustomObject]@{
                    path      = $info.FullName
                    name      = $info.Name
                    ext       = $ext
                    bytes     = $info.Length
                    sizeMB    = [math]::Round($info.Length / 1MB, 2)
                    sizeGB    = [math]::Round($info.Length / 1GB, 3)
                    modified  = $info.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
                    isSystem  = $isSystem
                })
            } catch { }
        }
    }
}

$topFiles = @($files | Sort-Object -Property bytes -Descending | Select-Object -First $Top)

# Folder level rollup: which directories hold the most space.
$folders = @{}
foreach ($f in $files) {
    $dir = Split-Path -Parent $f.path
    if (-not $dir) { continue }
    if (-not $folders.ContainsKey($dir)) {
        $folders[$dir] = [PSCustomObject]@{
            path    = $dir
            bytes   = [int64]0
            files   = 0
        }
    }
    $folders[$dir].bytes += $f.bytes
    $folders[$dir].files += 1
}

# Roll the per-file directories up into their parents as well, so a folder
# that only contains large files deep down still shows up.
$rollup = @{}
foreach ($dir in $folders.Keys) {
    $parts = $dir -split '\\'
    for ($i = 2; $i -le $parts.Length; $i++) {
        $parent = ($parts[0..($i - 1)] -join '\')
        if (-not $rollup.ContainsKey($parent)) {
            $rollup[$parent] = [PSCustomObject]@{
                path  = $parent
                bytes = [int64]0
                files = 0
            }
        }
        $rollup[$parent].bytes += $folders[$dir].bytes
        $rollup[$parent].files += $folders[$dir].files
    }
}

$topFolders = @($rollup.Values |
    Where-Object { $_.path -ne $scanRoot -and $_.path.Length -gt 3 } |
    Sort-Object -Property bytes -Descending |
    Select-Object -First 40 |
    ForEach-Object {
        [PSCustomObject]@{
            path   = $_.path
            sizeMB = [math]::Round($_.bytes / 1MB, 2)
            sizeGB = [math]::Round($_.bytes / 1GB, 3)
            files  = $_.files
        }
    })

# Aggregate by file type so the UI can show what is eating the disk.
$byType = @{}
foreach ($f in $files) {
    $key = if ($f.ext) { $f.ext } else { 'NO EXTENSION' }
    if (-not $byType.ContainsKey($key)) {
        $byType[$key] = [PSCustomObject]@{ ext = $key; bytes = [int64]0; files = 0 }
    }
    $byType[$key].bytes += $f.bytes
    $byType[$key].files += 1
}

$types = @($byType.Values |
    Sort-Object -Property bytes -Descending |
    Select-Object -First 25 |
    ForEach-Object {
        [PSCustomObject]@{
            ext    = $_.ext
            sizeMB = [math]::Round($_.bytes / 1MB, 2)
            sizeGB = [math]::Round($_.bytes / 1GB, 3)
            files  = $_.files
        }
    })

[PSCustomObject]@{
    drive       = $root
    minSizeMB   = $MinSizeMB
    scannedDirs = $scannedDirs
    scannedFiles = $scannedFiles
    skippedLinks = $skipped
    totalFound  = $files.Count
    files       = $topFiles
    folders     = $topFolders
    types       = $types
} | ConvertTo-Json -Depth 5
