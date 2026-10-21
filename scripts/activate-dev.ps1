param([string]$Customer='JP', [string]$SigningKey=(Join-Path $env:USERPROFILE '.jp-vision\issuer\owner.private'), [switch]$Import)
. $PSScriptRoot\common.ps1
Push-Location $JpvRoot
try {
    New-Item -ItemType Directory -Path 'artifacts/activation' -Force | Out-Null
    Invoke-JpvChecked 'artifacts/native/Release/JPVision.RuntimeCli.exe' @('request','artifacts/activation/current.vreq')
    Invoke-JpvChecked dotnet @('tools/LicenseIssuer/bin/Release/net10.0-windows/JPVision.LicenseIssuer.dll','issue','--request','artifacts/activation/current.vreq','--key',$SigningKey,'--out','artifacts/activation/current.vlic','--customer',$Customer)
    if($Import){Invoke-JpvChecked 'artifacts/native/Release/JPVision.RuntimeCli.exe' @('import','artifacts/activation/current.vlic')}
    Write-Output 'Development license signed normally. It is excluded from client packages.'
} finally { Pop-Location }
