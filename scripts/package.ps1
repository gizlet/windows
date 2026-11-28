# Builds the Launcher and its installer for one architecture.
#
#   scripts/package.ps1 -Version 1.2.3 [-Arch x64|arm64] [-Output artifacts]
#
# Publishes Gizlet.Launcher self-contained and Gizlet.FlashHelper as one Native AOT executable
# into one folder, checks that the elevated helper imports only DLLs Windows loads from System32, compiles
# installer/GizletLauncher.iss with Inno Setup 7 and writes
# <Output>/installer/GizletSetup-<version>-<arch>.exe.
#
# Signing: when GIZLET_SIGNTOOL_ARGS is set (signtool's certificate arguments, such as
# "/f cert.pfx /p ..." or Azure Trusted Signing's "/dlib ... /dmdf ..."), the Launcher, the flash
# helper, the uninstaller and the installer are Authenticode-signed with a SHA-256 digest and an
# RFC 3161 timestamp, and every signature is verified. GIZLET_REQUIRE_SIGNATURE=1 fails the build
# when they are not.
param(
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$Output = 'artifacts',
    # Rehearsals only: the base64 of a throwaway ssh-ed25519 update key the Launcher trusts instead
    # of the release key. The Release workflow never passes it.
    [string]$TestUpdateKey
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
if ($Version -notmatch '^(0|[1-9]\d{0,8})(\.(0|[1-9]\d{0,8})){2}$') { throw "Not a MAJOR.MINOR.PATCH version: $Version" }

$rid = "win-$Arch"
$output = [IO.Path]::GetFullPath($Output)
$publish = Join-Path $output "publish/$rid"
$installerDir = Join-Path $output 'installer'
Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publish, $installerDir | Out-Null

# Tool output goes to the console, not the pipeline: the script's only output is the installer path.
function Invoke([string]$file, [string[]]$arguments) {
    & $file @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$file exited with $LASTEXITCODE" }
}

$launcherArgs = @('publish', 'src/Gizlet.Launcher/Gizlet.Launcher.csproj', '--configuration', 'Release', '--runtime', $rid,
    '--self-contained', 'true', "-p:Version=$Version", "-p:Platform=$Arch", '-p:PublishReadyToRun=true', '-p:GizletPackaged=true',
    '--output', $publish)
if ($TestUpdateKey) {
    if ($TestUpdateKey -notmatch '^AAAAC3NzaC1lZDI1NTE5[A-Za-z0-9+/]{40,}$') { throw 'TestUpdateKey is not an ssh-ed25519 public key blob' }
    if ($env:GITHUB_WORKFLOW -eq 'Release') { throw 'A release never trusts a test update key' }
    Write-Warning 'Rehearsal build: trusting a test update key'
    $launcherArgs += "-p:GizletTestUpdateKey=$TestUpdateKey"
}
Invoke dotnet $launcherArgs

# The helper runs elevated from this user-writable folder, so it is one native executable with
# nothing beside it to replace, and only System32 DLLs (Windows' KnownDLLs and API sets) load into it.
$helperOut = Join-Path $output "publish/helper-$rid"
Remove-Item -Recurse -Force $helperOut -ErrorAction SilentlyContinue
Invoke dotnet @('publish', 'src/Gizlet.FlashHelper/Gizlet.FlashHelper.csproj', '--configuration', 'Release', '--runtime', $rid,
    "-p:Version=$Version", '--output', $helperOut)
Copy-Item -LiteralPath (Join-Path $helperOut 'Gizlet.FlashHelper.exe') -Destination $publish -Force
foreach ($stale in 'Gizlet.FlashHelper.dll', 'Gizlet.FlashHelper.deps.json', 'Gizlet.FlashHelper.runtimeconfig.json') {
    if (Test-Path (Join-Path $publish $stale)) { throw "$stale must not be shipped beside the helper" }
}
& (Join-Path $PSScriptRoot 'check-imports.ps1') (Join-Path $publish 'Gizlet.FlashHelper.exe')
foreach ($exe in 'Gizlet.Launcher.exe', 'Gizlet.FlashHelper.exe') {
    if (-not (Test-Path (Join-Path $publish $exe))) { throw "$exe was not published" }
}

# --- signing
$signArgs = $env:GIZLET_SIGNTOOL_ARGS
$signtool = $null
if ($signArgs) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signtool = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'signtool.exe not found in the Windows SDK' }
}
$timestamp = if ($env:GIZLET_TIMESTAMP_URL) { $env:GIZLET_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
$signLine = if ($signtool) { "`"$signtool`" sign /fd SHA256 /tr $timestamp /td SHA256 $signArgs" } else { $null }

# GIZLET_SIGNTOOL_ARGS as signtool's own arguments: words, or "quoted words" kept whole.
$signArgList = @([regex]::Matches("$signArgs", '"([^"]*)"|(\S+)') | ForEach-Object { if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value } })

function Sign([string]$file) {
    if (-not $signtool) { return }
    Invoke $signtool (@('sign', '/fd', 'SHA256', '/tr', $timestamp, '/td', 'SHA256') + $signArgList + @($file))
}

function Assert-Signed([string]$file) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) {
        throw "$file is not validly signed and timestamped: $($signature.Status) $($signature.StatusMessage)"
    }
    Write-Host "Signed: $(Split-Path -Leaf $file) by $($signature.SignerCertificate.Subject)"
}

Sign (Join-Path $publish 'Gizlet.Launcher.exe')
Sign (Join-Path $publish 'Gizlet.FlashHelper.exe')

# --- installer
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 7 (ISCC.exe) not found; run scripts/install-inno.ps1' }
$isccArgs = @("/DAppVersion=$Version", "/DArch=$Arch", "/DSource=$publish", "/DOutput=$installerDir", '/Qp')
# ISCC reads its command line without backslash escapes: quotes inside the sign command are $q.
if ($signLine) { $isccArgs += @('/DSign=1', ('/Ssign=' + $signLine.Replace('"', '$q') + ' $f')) }
Invoke $iscc ($isccArgs + @('installer/GizletLauncher.iss'))
$installer = Join-Path $installerDir "GizletSetup-$Version-$Arch.exe"
if (-not (Test-Path $installer)) { throw "$installer was not built" }
Sign $installer

if ($signLine -or $env:GIZLET_REQUIRE_SIGNATURE -eq '1') {
    Assert-Signed (Join-Path $publish 'Gizlet.Launcher.exe')
    Assert-Signed (Join-Path $publish 'Gizlet.FlashHelper.exe')
    Assert-Signed $installer
}
Write-Host "Installer: $installer"
Write-Output $installer
