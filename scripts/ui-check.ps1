# Opens the installed Launcher's real window and walks its pages, as a user would, saving a
# screenshot of each: Apps, Store, Flash a card and Settings in English and light, then Apps again
# in Simplified Chinese and dark from a profile's settings. Fails when the Launcher does not show
# a window or exits on its own. Uses its own GIZLET_PREFIX, so no user data is touched.
#
#   scripts/ui-check.ps1 -Launcher <Gizlet.Launcher.exe> -Output <folder for PNGs>
param([Parameter(Mandatory)][string]$Launcher, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace Gizlet -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
public struct RECT { public int Left, Top, Right, Bottom; }
'@
[void][Gizlet.Win]::SetProcessDPIAware()
New-Item -ItemType Directory -Force $Output | Out-Null
$prefix = Join-Path ([IO.Path]::GetTempPath()) ('gizlet-ui-' + [guid]::NewGuid().ToString('N'))
$env:GIZLET_PREFIX = $prefix

function Open-Launcher {
    $process = Start-Process -FilePath $Launcher -PassThru
    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited) { Show-Log; throw "The Launcher exited with $($process.ExitCode) before showing a window" }
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0) { Start-Sleep -Seconds 6; return $process }
    }
    Show-Log
    throw 'The Launcher showed no window within 60 seconds'
}

function Show-Log {
    $log = Join-Path $prefix 'device/launcher.log'
    if (Test-Path $log) { Write-Host '--- launcher.log'; Get-Content $log | Select-Object -Last 40 | Write-Host }
}

function Snap($process, [string]$name) {
    if ($process.HasExited) { Show-Log; throw "The Launcher exited with $($process.ExitCode) on $name" }
    $window = $process.MainWindowHandle
    [void][Gizlet.Win]::ShowWindow($window, 9)
    [void][Gizlet.Win]::SetForegroundWindow($window)
    Start-Sleep -Milliseconds 800
    $rect = New-Object Gizlet.Win+RECT
    [void][Gizlet.Win]::GetWindowRect($window, [ref]$rect)
    $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
    if ($width -lt 200 -or $height -lt 200) { throw "The window is $width x $height" }
    $bitmap = New-Object Drawing.Bitmap $width, $height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save((Join-Path $Output "$name.png"), [Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Write-Host "Saved $name.png ($width x $height)"
}

function Press($process, [string]$keys, [int]$wait = 4) {
    [void][Gizlet.Win]::SetForegroundWindow($process.MainWindowHandle)
    Start-Sleep -Milliseconds 400
    [Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Seconds $wait
}

try {
    $launcher = Open-Launcher
    Snap $launcher '1-apps'
    Press $launcher '^2' 10; Snap $launcher '2-store'
    Press $launcher '^3' 6; Snap $launcher '3-flash'
    Press $launcher '^,' 4; Snap $launcher '4-settings'
    Stop-Process -Id $launcher.Id -Force
    Start-Sleep -Seconds 2

    # The same profile, now in Simplified Chinese and dark.
    $profile = Get-ChildItem (Join-Path $prefix 'profiles') -Directory | Where-Object { $_.Name -notlike '.*' } | Select-Object -First 1
    if (-not $profile) { throw 'The first start made no profile' }
    '{"language":"zh-CN","theme":"dark","favorites":[],"recent":[]}' | Set-Content -LiteralPath (Join-Path $profile.FullName 'settings.json') -Encoding utf8
    $launcher = Open-Launcher
    Snap $launcher '5-apps-zh-dark'
    Press $launcher '^2' 10; Snap $launcher '6-store-zh-dark'
    Stop-Process -Id $launcher.Id -Force
    Show-Log
    Write-Host 'UI check passed'
} finally {
    Get-Process -Name 'Gizlet.Launcher' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Remove-Item -Recurse -Force $prefix -ErrorAction SilentlyContinue
}
