# Development helper: resolves the shared taxonomy and prints what was found.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot '..\targets.ps1')

$drive = if ($args.Count -gt 0) { $args[0] } else { 'C:' }
Set-OmniCleanActiveDrive $drive | Out-Null

$total = 0
$detected = 0
foreach ($t in (Get-OmniCleanTargets)) {
    $paths = @(Get-OmniCleanTargetPaths $t)
    $size = Get-OmniCleanTargetSizeMB $t
    $total++
    if ($paths.Count -gt 0) { $detected++ }
    Write-Output ("{0,-24} {1,-16} {2,10} MB  {3,4} paths  [{4}]" -f $t.id, $t.kind, $size, $paths.Count, $t.groupId)
    foreach ($p in ($paths | Select-Object -First 6)) {
        Write-Output ("      -> {0}" -f $p)
    }
}

Write-Output ""
Write-Output ("{0}/{1} targets detected" -f $detected, $total)
