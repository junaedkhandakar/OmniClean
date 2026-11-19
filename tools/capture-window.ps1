# capture-window.ps1 - Capture the OmniClean main window to a PNG.
#
#   powershell -ExecutionPolicy Bypass -File tools/capture-window.ps1 -OutPath docs/screenshot.png
#
# The product page shows a real screenshot of the shipped application, so
# this captures the actual window rather than a mock-up.
#
# Two details matter here:
#
#  * The window is printed with PrintWindow(PW_RENDERFULLCONTENT) rather
#    than grabbed from the screen, because a screen copy would also
#    capture whatever else happens to be in front of the window.
#  * The script opts into per-monitor DPI awareness before measuring the
#    window. OmniClean.exe is Per-Monitor V2 aware, so on a 200% display
#    the window is drawn at 2440 physical pixels while a non-aware caller
#    reads back a virtualized 1220 - which would capture only the
#    top-left quarter of the window.

param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\OmniClean.exe'),
    [string]$OutPath = (Join-Path $PSScriptRoot '..\docs\screenshot.png'),
    [string]$NavButton = 'Storage Cleaner',
    [int]$SettleSeconds = 10
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $ExePath)) {
    Write-Error "Executable not found: $ExePath"
    exit 1
}

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class OmniCapture {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
'@

# -4 = PER_MONITOR_AWARE_V2. This has to happen before any window is
# measured, otherwise the rectangle comes back in virtualized pixels.
[void][OmniCapture]::SetProcessDpiAwarenessContext([IntPtr](-4))
if (-not $?) {
    [void][OmniCapture]::SetProcessDPIAware()
}

# Any earlier instance would block the single-instance guard and leave the
# capture showing a stale window, so clear them out first.
Get-Process -Name 'OmniClean' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}
Start-Sleep -Milliseconds 500

$proc = Start-Process -FilePath $ExePath -PassThru

# Wait for the main window handle to appear.
$deadline = (Get-Date).AddSeconds(30)
while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    $proc.Refresh()
}

if ($proc.MainWindowHandle -eq 0) {
    try { $proc.Kill() } catch { }
    Write-Error 'The application did not open a window.'
    exit 1
}

$handle = $proc.MainWindowHandle

# Restore to the natural window size. Maximizing would push the window
# past the display, where DWM never renders it.
[void][OmniCapture]::ShowWindow($handle, 9)   # SW_RESTORE
[void][OmniCapture]::SetForegroundWindow($handle)
Start-Sleep -Milliseconds 800

# Let the first scan settle so the numbers in the capture are real.
Start-Sleep -Seconds $SettleSeconds

# Select the tab we want to show. The window does open on the Storage
# Cleaner tab, but the previous run's tab can linger, so the navigation
# button is clicked explicitly through UI Automation.
if ($NavButton) {
    try {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes

        $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button
        )
        $buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)

        foreach ($button in $buttons) {
            if ($button.Current.Name -eq $NavButton) {
                $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $pattern.Invoke()
                Write-Host "Selected tab: $NavButton"
                Start-Sleep -Milliseconds 1200
                break
            }
        }
    } catch {
        Write-Host "Tab selection skipped: $($_.Exception.Message)"
    }
}

$rect = New-Object OmniCapture+RECT
if (-not [OmniCapture]::GetWindowRect($handle, [ref]$rect)) {
    try { $proc.CloseMainWindow() | Out-Null } catch { }
    Write-Error 'Could not read the window rectangle.'
    exit 1
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

if ($width -le 0 -or $height -le 0) {
    try { $proc.CloseMainWindow() | Out-Null } catch { }
    Write-Error 'The window reported an empty rectangle.'
    exit 1
}

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()

# Flag 2 = PW_RENDERFULLCONTENT, required for composited / DWM windows.
[void][OmniCapture]::PrintWindow($handle, $hdc, 2)
$graphics.ReleaseHdc($hdc)
$graphics.Dispose()

$outFull = [System.IO.Path]::GetFullPath($OutPath)
$outDir = [System.IO.Path]::GetDirectoryName($outFull)
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$bitmap.Save($outFull, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

try { $proc.CloseMainWindow() | Out-Null } catch { }
try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }

Write-Host "Captured ${width}x${height} to $outFull"
exit 0
