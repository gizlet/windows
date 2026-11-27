# Rehearses an in-app update end to end on Windows, as the first release's acceptance asks:
#
#  1. installs the Old Launcher (signed) for this user, and gives it real data: The Little Prince
#     from the live Store, profiles A and B, a favourite and a save in A, B in Simplified Chinese;
#  2. starts it against a local update server that has nothing yet, so its first check finds
#     nothing;
#  3. publishes the New version: the signed installer and a signed update.json, as
#     devops/scripts/release.py writes them;
#  4. waits for the running Launcher's next 120-second poll to show the update banner, and clicks
#     "Update and restart" through UI Automation;
#  5. checks that the Launcher was replaced by the New version, opened again on its own, and kept
#     the installed app, both profiles, the favourite, the save and the settings.
#
# Both installers must be signed by the same publisher, trusted on this machine, and built with
# -TestUpdateKey for the public half of -Key. Screenshots go to -Output.
param(
    [Parameter(Mandatory)][string]$Old,
    [Parameter(Mandatory)][string]$New,
    [Parameter(Mandatory)][string]$NewVersion,
    [Parameter(Mandatory)][string]$Key,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Namespace Gizlet -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
public struct RECT { public int Left, Top, Right, Bottom; }
'@
[void][Gizlet.Win]::SetProcessDPIAware()
New-Item -ItemType Directory -Force $Output | Out-Null
$app = Join-Path $env:LOCALAPPDATA 'Programs\Gizlet Launcher'
$exe = Join-Path $app 'Gizlet.Launcher.exe'
$prefix = Join-Path ([IO.Path]::GetTempPath()) ('gizlet-rehearsal-' + [guid]::NewGuid().ToString('N'))
$site = Join-Path ([IO.Path]::GetTempPath()) ('gizlet-site-' + [guid]::NewGuid().ToString('N'))
$port = 8765
$server = $null
function Step([string]$text) { Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $text" }
function Check([bool]$ok, [string]$what) { if (-not $ok) { Show-Log; throw "FAILED: $what" }; Step "ok   $what" }
function Show-Log {
    $log = Join-Path $prefix 'device/launcher.log'
    if (Test-Path $log) { Write-Host '--- launcher.log'; Get-Content $log | Select-Object -Last 40 | Write-Host }
}

function Window([int]$id, [int]$seconds = 60) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $p) { Show-Log; throw "The Launcher ($id) exited" }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { return $p.MainWindowHandle }
        Start-Sleep -Milliseconds 500
    }
    throw "The Launcher ($id) showed no window"
}

function Find([IntPtr]$window, [string]$name) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($window)
    $condition = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $name
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Snap([IntPtr]$window, [string]$name) {
    [void][Gizlet.Win]::SetForegroundWindow($window)
    Start-Sleep -Milliseconds 800
    $rect = New-Object Gizlet.Win+RECT
    [void][Gizlet.Win]::GetWindowRect($window, [ref]$rect)
    $bitmap = New-Object Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save((Join-Path $Output "$name.png"), [Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

function Installed-Version { (Get-Item $exe).VersionInfo.ProductVersion }

try {
    # 1. The old version, with data.
    $p = Start-Process -FilePath $Old -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    Check ($p.ExitCode -eq 0) "installs $(Split-Path -Leaf $Old)"
    $oldVersion = Installed-Version
    Check ((Get-AuthenticodeSignature $exe).Status -eq 'Valid') "the installed Launcher $oldVersion is validly signed"
    dotnet run --project tools/Gizlet.StoreCheck --configuration Release -- --query prince --id io.gizlet.littleprince --prefix $prefix --install-only
    Check ($LASTEXITCODE -eq 0) 'installs The Little Prince from the Store into the Launcher''s data, with profiles A and B'
    $before = @{
        Profiles = @(Get-ChildItem (Join-Path $prefix 'profiles') -Directory | Where-Object { $_.Name -notlike '.*' } | ForEach-Object Name | Sort-Object)
        Settings = @{}
    }
    foreach ($id in $before.Profiles) { $before.Settings[$id] = Get-Content -Raw (Join-Path $prefix "profiles/$id/settings.json") }

    # 2. The old Launcher, running, against a server with nothing published.
    New-Item -ItemType Directory -Force (Join-Path $site "windows/releases/v$NewVersion") | Out-Null
    $server = Start-Process -FilePath python -ArgumentList '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $site -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    $env:GIZLET_PREFIX = $prefix
    $env:GIZLET_UPDATE_URL = "http://127.0.0.1:$port/windows/update.json"
    try { $running = Start-Process -FilePath $exe -PassThru } finally { Remove-Item env:GIZLET_PREFIX, env:GIZLET_UPDATE_URL }
    $window = Window $running.Id
    Start-Sleep -Seconds 15
    Check ($null -eq (Find $window 'Update and restart')) "the running Launcher $oldVersion finds no update while none is published"
    Snap $window '1-old-running'

    # 3. Publish the new version, as release.py does.
    $installerName = "GizletSetup-$NewVersion-x64.exe"
    Copy-Item $New (Join-Path $site "windows/releases/v$NewVersion/$installerName")
    $sha = (Get-FileHash -Algorithm SHA256 $New).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        schema = 1; name = 'gizlet-windows'; version = $NewVersion; tag = "v$NewVersion"; commit = ('0' * 40)
        published_at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        notes = [ordered]@{ en = "Rehearsal $NewVersion."; 'zh-CN' = "演练版本 $NewVersion。" }
        installers = [ordered]@{ x64 = [ordered]@{ path = $installerName; sha256 = $sha; size = (Get-Item $New).Length } }
    } | ConvertTo-Json -Depth 5
    $work = Join-Path ([IO.Path]::GetTempPath()) ('gizlet-sign-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $work | Out-Null
    [IO.File]::WriteAllText((Join-Path $work 'manifest.json'), $manifest, [Text.UTF8Encoding]::new($false))
    & ssh-keygen -q -Y sign -f $Key -n gizlet-windows-update (Join-Path $work 'manifest.json')
    Check ($LASTEXITCODE -eq 0) 'signs update.json with the update key'
    $envelope = [ordered]@{ manifest = $manifest; signature = (Get-Content -Raw (Join-Path $work 'manifest.json.sig')) } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $site 'windows/update.json'), $envelope, [Text.UTF8Encoding]::new($false))
    $published = Get-Date
    Step "published $NewVersion"

    # 4. The next poll finds it; the user agrees.
    $button = $null
    while (-not $button -and ((Get-Date) - $published).TotalSeconds -lt 150) {
        Start-Sleep -Seconds 2
        $window = Window $running.Id
        $button = Find $window 'Update and restart'
    }
    $found = [int]((Get-Date) - $published).TotalSeconds
    Check ($null -ne $button) "the running Launcher shows the $NewVersion banner $found s after publishing"
    Check ($found -le 125) 'within its next 120-second poll'
    Check ($null -ne (Find $window "Gizlet Launcher $NewVersion is available")) 'the banner names the version'
    Check ($null -ne (Find $window "Rehearsal $NewVersion.")) 'and shows its notes'
    Snap $window '2-update-offered'
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Step 'clicked Update and restart'

    # 5. Replaced, reopened, data kept.
    Check ($running.WaitForExit(120000)) 'the old Launcher exits to install'
    $reopened = $null
    for ($i = 0; $i -lt 180 -and -not $reopened; $i++) {
        Start-Sleep -Seconds 1
        $reopened = Get-Process -Name 'Gizlet.Launcher' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
    }
    Check ($null -ne $reopened) 'the installer opens the Launcher again'
    $newInstalled = Installed-Version
    Check ($newInstalled -like "$NewVersion*") "the installed Launcher is now $newInstalled"
    Check ((Get-AuthenticodeSignature $exe).Status -eq 'Valid') 'and validly signed'
    $window = Window $reopened.Id
    Start-Sleep -Seconds 8
    Check ($null -ne (Find $window 'The Little Prince')) 'the reopened Launcher lists The Little Prince among its apps'
    Snap $window '3-new-running'
    $after = @(Get-ChildItem (Join-Path $prefix 'profiles') -Directory | Where-Object { $_.Name -notlike '.*' } | ForEach-Object Name | Sort-Object)
    Check (($after -join ',') -eq ($before.Profiles -join ',')) "both profiles are kept ($($after -join ', '))"
    foreach ($id in $before.Profiles) {
        Check ((Get-Content -Raw (Join-Path $prefix "profiles/$id/settings.json")) -eq $before.Settings[$id]) "profile $id keeps its settings and favourites"
    }
    $save = Get-ChildItem (Join-Path $prefix 'profiles') -Recurse -Filter 'rehearsal-save.txt' | Select-Object -First 1
    Check ($save -and (Get-Content $save.FullName) -eq 'kept') 'the save is kept'
    Check (Test-Path (Join-Path $prefix 'device/apps/io.gizlet.littleprince/manifest.json')) 'the installed app is kept'
    Check ((Get-Content -Raw (Join-Path $prefix 'device/store/origins.json')) -match 'github.com/gizlet/little-prince') 'and still updates from its repository'
    Step 'Update rehearsal passed'
} finally {
    Show-Log
    Get-Process -Name 'Gizlet.Launcher' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}
