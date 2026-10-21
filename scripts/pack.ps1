param([ValidateSet('Release')][string]$Configuration='Release')
. $PSScriptRoot\common.ps1
Initialize-JpvEnvironment -RuntimeOnly
Push-Location $JpvRoot
try {
    $version='0.7.2'
    $release=Join-Path $JpvRoot "artifacts\release\JP-Vision-$version"
    # Use an explicit destination and preserve any existing release directory.
    if(Test-Path -LiteralPath $release){$release="$release-$(Get-Date -Format yyyyMMdd-HHmmss)"}
    New-Item -ItemType Directory -Path "$release\Studio","$release\WpfSample","$release\Docs" -Force | Out-Null
    Invoke-JpvChecked dotnet @('publish','managed/JPVision.Studio.Wpf/JPVision.Studio.Wpf.csproj','-c','Release','-r','win-x64','--self-contained','true','-o',"$release\Studio")
    Invoke-JpvChecked dotnet @('publish','samples/WpfSample/WpfSample.csproj','-c','Release','-r','win-x64','--self-contained','false','-o',"$release\WpfSample")
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $crt=Get-ChildItem (Join-Path $vs 'VC/Redist/MSVC/*/x64/Microsoft.VC145.CRT/vcruntime140.dll') -ErrorAction Stop | Sort-Object FullName -Descending | Select-Object -First 1
    if(-not $crt){throw 'Matching release VC++ redistributable DLLs missing.'}
    $crtFolder=Split-Path $crt.FullName -Parent
    foreach($file in Get-ChildItem -LiteralPath $crtFolder -Filter '*.dll'){
        Copy-Item -LiteralPath $file.FullName -Destination "$release/Studio/$($file.Name)"
        Copy-Item -LiteralPath $file.FullName -Destination "$release/WpfSample/$($file.Name)"
        Copy-Item -LiteralPath $file.FullName -Destination "artifacts/native/Release/$($file.Name)" -Force
    }
    Invoke-JpvChecked dotnet @('pack','managed/JPVision.Sdk/JPVision.Sdk.csproj','-c','Release','-o','artifacts/packages')
    Invoke-JpvChecked dotnet @('pack','managed/JPVision.Wpf/JPVision.Wpf.csproj','-c','Release','-o','artifacts/packages')
    $consumerStamp=Get-Date -Format yyyyMMdd-HHmmss
    $packageStore=Join-Path $JpvRoot "artifacts/consumer-packages/$consumerStamp"
    Invoke-JpvChecked dotnet @('restore','samples/PackageConsumer/PackageConsumer.csproj','--force-evaluate','--source',(Join-Path $JpvRoot 'artifacts/packages'),'--packages',$packageStore)
    $consumer=Join-Path $JpvRoot "artifacts/consumer/$consumerStamp"
    Invoke-JpvChecked dotnet @('publish','samples/PackageConsumer/PackageConsumer.csproj','-c','Release','-r','win-x64','--self-contained','false','--no-restore','-o',$consumer)
    Copy-Item -Path 'samples/recipes' -Destination "$consumer\recipes" -Recurse -Force
    # Run from its own directory with no developer Qt/OpenCV PATH entries.
    $originalPath=$env:PATH
    try { $env:PATH="$env:SystemRoot\system32;$env:SystemRoot;$env:ProgramFiles\dotnet";Push-Location $consumer
        try {
            foreach($name in @('parts-count','circles','lines','regions','line-caliper','circle-caliper','template-match','grayscale','circle-measurement','circle-caliper-measurement','fast-match','fast-locate')){
                Invoke-JpvChecked (Join-Path $consumer 'PackageConsumer.exe') @("recipes/$name.vrecipe")
            }
        } finally { Pop-Location }
    } finally { $env:PATH=$originalPath }
    $consumer | Set-Content 'artifacts/consumer/latest.txt' -Encoding utf8
    Copy-Item -Path 'docs/*.md','README.md','THIRD-PARTY-NOTICES.md' -Destination "$release\Docs"
    Copy-Item -LiteralPath 'third_party/json-LICENSE.MIT' -Destination "$release\Docs\nlohmann-json-LICENSE.txt"
    Copy-Item -LiteralPath 'third_party/nodeeditor/LICENSE.rst' -Destination "$release\Docs\QtNodes-LICENSE.txt"
    Copy-Item -Path 'third_party/licenses/*' -Destination "$release\Docs"
    $files=Get-ChildItem -LiteralPath $release -Recurse -File
    $forbidden=$files | Where-Object {$_.Extension -in @('.vlic','.private','.key','.pem','.vreq') -or $_.Name -match 'LicenseIssuer|GraphTests|NativeTests|trusted_key.h'}
    if($forbidden){throw "Client package contains forbidden developer material: $($forbidden.Name -join ', ')"}
    if(Get-ChildItem -LiteralPath $release -Filter 'Qt*.dll' -Recurse){throw 'WPF release must not contain Qt.'}
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach($package in Get-ChildItem 'artifacts/packages/*.nupkg'){
        $zip=[IO.Compression.ZipFile]::OpenRead($package.FullName)
        try{foreach($entry in $zip.Entries){if($entry.FullName -match '(?i)LicenseIssuer|Qt[0-9].*dll|\.(vlic|private|pem|key|vreq)$'){throw "Forbidden NuGet entry: $($entry.FullName)"}}}finally{$zip.Dispose()}
    }
    $files | ForEach-Object {[pscustomobject]@{path=$_.FullName.Substring($release.Length+1);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}} | ConvertTo-Json -Depth 4 | Set-Content "$release\manifest.json" -Encoding utf8
    $release | Set-Content 'artifacts/release/latest.txt' -Encoding utf8
    Compress-Archive -Path "$release\*" -DestinationPath "$release.zip" -CompressionLevel Optimal
    Write-Output "Client release: $release.zip; NuGet: artifacts/packages. No licenses or signing keys included."
} finally { Pop-Location }
