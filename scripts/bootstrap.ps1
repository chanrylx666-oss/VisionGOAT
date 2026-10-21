param([switch]$InitializeOwner, [string]$SigningKey = (Join-Path $env:USERPROFILE '.jp-vision\issuer\owner.private'))
. $PSScriptRoot\common.ps1
Initialize-JpvEnvironment
Push-Location $JpvRoot
try {
    foreach ($file in @('../include/opencv2/core/version.hpp','../lib/opencv_world500.lib','../lib/opencv_world500d.lib','../bin/opencv_world500.dll','../bin/opencv_world500d.dll')) {
        if (-not (Test-Path -LiteralPath $file)) { throw "Existing OpenCV 5 distribution missing: $file" }
    }
    if (-not (Test-Path -LiteralPath 'third_party\nodeeditor\CMakeLists.txt')) {
        Invoke-JpvChecked git @('clone','--depth','1','--branch','3.0.16','https://github.com/paceholder/nodeeditor.git','third_party/nodeeditor')
    }
    $commit = & git -C third_party/nodeeditor rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -ne '7c6341a66a8e46b8988140b9e60d892b6a3560b3') { throw 'QtNodes dependency differs from the locked commit.' }
    if (-not (Test-Path -LiteralPath 'third_party\json.hpp')) { throw 'Missing vendored nlohmann/json 3.12.0.' }
    if((Get-FileHash 'third_party/json.hpp' -Algorithm SHA256).Hash -ne 'AAF127C04CB31C406E5B04A63F1AE89369FCCDE6D8FA7CDDA1ED4F32DFC5DE63'){throw 'Vendored JSON dependency differs from the locked digest.'}
    Invoke-JpvChecked dotnet @('build','tools/LicenseIssuer/LicenseIssuer.csproj','-c','Release')
    if ($InitializeOwner) {
        if ((Test-Path -LiteralPath 'config/trusted-key.json') -and -not (Test-Path -LiteralPath $SigningKey)) { throw 'A trust anchor already exists. Restore its owner signing key; do not silently replace it.' }
        $public = Join-Path $JpvRoot 'artifacts\owner-public.json'
        Invoke-JpvChecked dotnet @('tools/LicenseIssuer/bin/Release/net10.0-windows/JPVision.LicenseIssuer.dll','keygen','--key',$SigningKey,'--public',$public)
        if (Test-Path -LiteralPath 'config/trusted-key.json') {
            $existing = Get-Content 'config/trusted-key.json' -Raw | ConvertFrom-Json
            $generated = Get-Content $public -Raw | ConvertFrom-Json
            if ($existing.keyId -ne $generated.keyId -or $existing.x -ne $generated.x -or $existing.y -ne $generated.y) { throw 'Owner key does not match the compiled trust anchor.' }
        } else { Copy-Item -LiteralPath $public -Destination 'config/trusted-key.json' }
        Invoke-JpvChecked dotnet @('tools/LicenseIssuer/bin/Release/net10.0-windows/JPVision.LicenseIssuer.dll','export-header','--public','config/trusted-key.json','--out','config/trusted_key.h')
    }
    if (-not (Test-Path -LiteralPath 'config/trusted_key.h')) { throw 'Initialize the owner trust anchor with -InitializeOwner.' }
    $preset = @{ version=6; configurePresets=@(
        @{name='studio-local-release'; inherits='studio-release'; cacheVariables=@{CMAKE_PREFIX_PATH=$env:JPV_QT_ROOT;CMAKE_CXX_COMPILER="$env:JPV_MINGW_ROOT/bin/g++.exe";CMAKE_MAKE_PROGRAM=$env:JPV_NINJA}},
        @{name='studio-local-debug'; inherits='studio-debug'; cacheVariables=@{CMAKE_PREFIX_PATH=$env:JPV_QT_ROOT;CMAKE_CXX_COMPILER="$env:JPV_MINGW_ROOT/bin/g++.exe";CMAKE_MAKE_PROGRAM=$env:JPV_NINJA}}
    )}
    $preset | ConvertTo-Json -Depth 8 | Set-Content 'CMakeUserPresets.json' -Encoding utf8
    Write-Output 'Dependencies checked; local Qt Creator presets written. Owner key is outside the project.'
} finally { Pop-Location }
