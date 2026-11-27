# Installs Inno Setup 7 for the current user when it is not installed, checking the download
# against a pinned SHA-256.
$ErrorActionPreference = 'Stop'
$version = '7.1.1'
$sha256 = '8A5A6711C3766A6E5FB74F280BA0E3551E719CE94538D901FDBD1F8F9920B3F0'
$found = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($found) { Write-Host "Inno Setup: $found"; return }
$setup = Join-Path ([IO.Path]::GetTempPath()) "innosetup-$version-x64.exe"
Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/jrsoftware/issrc/releases/download/is-$($version -replace '\.', '_')/innosetup-$version-x64.exe" -OutFile $setup
if ((Get-FileHash -Algorithm SHA256 $setup).Hash -ne $sha256) { throw 'Inno Setup download does not match its pinned SHA-256' }
$process = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/SP-' -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Inno Setup installer exited with $($process.ExitCode)" }
Remove-Item -Force $setup
Write-Host "Inno Setup $version installed"
