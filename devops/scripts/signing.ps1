# Prepares Authenticode signing on the Release workflow's Windows runner and exports
# GIZLET_SIGNTOOL_ARGS for scripts/package.ps1. Reads, from the environment (decrypted from
# devops/secrets/release.enc.yaml by the workflow), one of:
#   CODESIGN_PFX_BASE64, CODESIGN_PFX_PASSWORD      a PKCS#12 code-signing certificate, or
#   ARTIFACT_SIGNING_ENDPOINT, ARTIFACT_SIGNING_ACCOUNT, ARTIFACT_SIGNING_PROFILE,
#   AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET   Azure Artifact (Trusted) Signing.
# Fails when neither is configured: a release is never published unsigned.
$ErrorActionPreference = 'Stop'
$temp = $env:RUNNER_TEMP
function Export([string]$name, [string]$value) {
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "$name=$value"
    Set-Item "env:$name" $value
}

if ($env:CODESIGN_PFX_BASE64) {
    Write-Host "::add-mask::$($env:CODESIGN_PFX_PASSWORD)"
    $pfx = Join-Path $temp 'codesign.pfx'
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:CODESIGN_PFX_BASE64))
    Export GIZLET_SIGNTOOL_ARGS "/f `"$pfx`" /p $($env:CODESIGN_PFX_PASSWORD)"
    Write-Host 'Signing with the PKCS#12 certificate'
    return
}

if ($env:ARTIFACT_SIGNING_ENDPOINT) {
    foreach ($name in 'ARTIFACT_SIGNING_ACCOUNT', 'ARTIFACT_SIGNING_PROFILE', 'AZURE_TENANT_ID', 'AZURE_CLIENT_ID', 'AZURE_CLIENT_SECRET') {
        if (-not (Get-Item "env:$name" -ErrorAction SilentlyContinue).Value) { throw "$name is not set" }
    }
    Write-Host "::add-mask::$($env:AZURE_CLIENT_SECRET)"
    $version = '1.0.151'
    $sha256 = '35375A4BC4E1102DA729FC3D8D9925F774C3BAAE6B8B7FF6398D948CA9AB0D8A'
    $package = Join-Path $temp "artifactsigning-$version.nupkg"
    Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.artifactsigning.client/$version/microsoft.artifactsigning.client.$version.nupkg" -OutFile $package
    if ((Get-FileHash -Algorithm SHA256 $package).Hash -ne $sha256) { throw 'Artifact Signing client does not match its pinned SHA-256' }
    $client = Join-Path $temp 'artifactsigning'
    Expand-Archive -LiteralPath $package -DestinationPath $client -Force
    $metadata = Join-Path $temp 'artifactsigning.json'
    @{
        Endpoint = $env:ARTIFACT_SIGNING_ENDPOINT
        CodeSigningAccountName = $env:ARTIFACT_SIGNING_ACCOUNT
        CertificateProfileName = $env:ARTIFACT_SIGNING_PROFILE
        ExcludeCredentials = @('ManagedIdentityCredential', 'WorkloadIdentityCredential', 'SharedTokenCacheCredential',
            'VisualStudioCredential', 'VisualStudioCodeCredential', 'AzureCliCredential', 'AzurePowerShellCredential',
            'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
    } | ConvertTo-Json | Set-Content -LiteralPath $metadata -Encoding utf8
    Export GIZLET_SIGNTOOL_ARGS "/dlib `"$client\bin\x64\Azure.CodeSigning.Dlib.dll`" /dmdf `"$metadata`""
    Export GIZLET_TIMESTAMP_URL 'http://timestamp.acs.microsoft.com'
    Write-Host 'Signing with Azure Artifact Signing'
    return
}

throw 'No code-signing credentials: add CODESIGN_PFX_* or ARTIFACT_SIGNING_* and AZURE_* to devops/secrets/release.enc.yaml'
