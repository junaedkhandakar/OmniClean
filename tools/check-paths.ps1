# Development helper: checks specific paths and reports size + existence.
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$paths = @(
    "$env:ProgramData\NVIDIA Corporation\NVIDIA app\UpdateFramework\ota-artifacts",
    "$env:ProgramData\NVIDIA Corporation\Downloader",
    "$env:ProgramData\NVIDIA Corporation\NV_Cache",
    "$env:LOCALAPPDATA\NVIDIA\DXCache",
    "$env:LOCALAPPDATA\NVIDIA\GLCache",
    "$env:LOCALAPPDATA\D3DSCache",
    "$env:LOCALAPPDATA\Intel\ShaderCache",
    "$env:LOCALAPPDATA\AMD\DxCache",
    "$env:WINDIR\WinSxS\Backup",
    "$env:WINDIR\System32\Config\TxF"
)

foreach ($p in $paths) {
    if (-not (Test-Path -LiteralPath $p)) {
        Write-Output ("MISSING  {0}" -f $p)
        continue
    }
    $sum = (Get-ChildItem -LiteralPath $p -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    $mb = if ($sum) { [math]::Round($sum / 1MB, 2) } else { 0 }
    Write-Output ("{0,10} MB  {1}" -f $mb, $p)
}

Write-Output ""
Write-Output "--- Video controllers ---"
Get-CimInstance Win32_VideoController | ForEach-Object {
    Write-Output ("{0}  driver={1}" -f $_.Name, $_.DriverVersion)
}
