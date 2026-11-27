# Builds and tests the repository as CI does, on Windows. Run from any directory.
#
# Unit tests, then the Windows tests, then an unsigned x64 installer, which is installed for the
# current user, started in smoke-test mode and uninstalled again, leaving user data alone.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Invoke([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file exited with $LASTEXITCODE" }
}

Invoke dotnet @('test', '--solution', 'Gizlet.Windows.slnx', '--configuration', 'Release')

& ./scripts/install-inno.ps1
$installer = & ./scripts/package.ps1 -Version 0.0.1 -Arch x64 -Output artifacts | Select-Object -Last 1

# Install, smoke-test and uninstall, as a user would, with no elevation.
$data = Join-Path $env:LOCALAPPDATA 'Gizlet'
$marker = Join-Path $data 'ci-marker.txt'
New-Item -ItemType Directory -Force $data | Out-Null
Set-Content -LiteralPath $marker -Value 'kept'
$app = Join-Path $env:LOCALAPPDATA 'Programs\Gizlet Launcher'
$p = Start-Process -FilePath $installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Installer exited with $($p.ExitCode)" }
foreach ($exe in 'Gizlet.Launcher.exe', 'Gizlet.FlashHelper.exe') {
    if (-not (Test-Path (Join-Path $app $exe))) { throw "$exe was not installed" }
}
$p = Start-Process -FilePath (Join-Path $app 'Gizlet.Launcher.exe') -ArgumentList '--smoke' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Launcher smoke test exited with $($p.ExitCode)" }
$uninstaller = Get-ChildItem $app -Filter 'unins*.exe' | Select-Object -First 1
$p = Start-Process -FilePath $uninstaller.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Uninstaller exited with $($p.ExitCode)" }
Start-Sleep -Seconds 2
if (Test-Path (Join-Path $app 'Gizlet.Launcher.exe')) { throw 'Uninstall left the Launcher behind' }
if ((Get-Content -LiteralPath $marker) -ne 'kept') { throw 'Uninstall touched user data' }
Remove-Item -LiteralPath $marker
Write-Host 'Install, smoke test and uninstall passed'
