# Builds and tests the repository as CI does. Run from any directory.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

dotnet test --solution Gizlet.Windows.slnx --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
