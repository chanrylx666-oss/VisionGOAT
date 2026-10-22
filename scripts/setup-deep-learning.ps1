param()
$ErrorActionPreference = 'Stop'
$jpvRoot = Split-Path $PSScriptRoot -Parent
$jpvVersion = '1.24.4'
$jpvSha256 = '4B978D5065B85E7004B6C6F60CA494BD978FBE6836CBF0A0B52D82B61AB99638'
$jpvDependency = Join-Path $jpvRoot 'artifacts\dependencies\onnxruntime'
$jpvRuntime = Join-Path $jpvDependency 'win-x64'
$jpvPackage = Join-Path $jpvDependency "microsoft.ml.onnxruntime.$jpvVersion.nupkg"
New-Item -ItemType Directory -Path $jpvRuntime -Force | Out-Null
if (-not (Test-Path -LiteralPath $jpvPackage)) {
    $jpvDownload = $jpvPackage + '.download'
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/$jpvVersion/microsoft.ml.onnxruntime.$jpvVersion.nupkg" -OutFile $jpvDownload
    if ((Get-FileHash -LiteralPath $jpvDownload -Algorithm SHA256).Hash -ne $jpvSha256) {
        throw 'ONNX Runtime package checksum mismatch. No runtime files were installed.'
    }
    Move-Item -LiteralPath $jpvDownload -Destination $jpvPackage -Force
}
if ((Get-FileHash -LiteralPath $jpvPackage -Algorithm SHA256).Hash -ne $jpvSha256) {
    throw 'Cached ONNX Runtime package checksum mismatch.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$jpvZip = [IO.Compression.ZipFile]::OpenRead($jpvPackage)
try {
    foreach ($jpvName in @('onnxruntime.dll', 'onnxruntime_providers_shared.dll')) {
        $jpvEntry = $jpvZip.GetEntry("runtimes/win-x64/native/$jpvName")
        if ($null -eq $jpvEntry) { throw "Missing package file: $jpvName" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($jpvEntry, (Join-Path $jpvRuntime $jpvName), $true)
    }
} finally { $jpvZip.Dispose() }
Write-Output "Installed optional ONNX Runtime CPU ${jpvVersion}: $jpvRuntime"
Write-Output 'Rebuild the native Debug target and WPF project in Visual Studio before F5.'
