param([ValidateSet('Debug','Release')][string]$Configuration='Release', [switch]$PublishStudio)
. $PSScriptRoot\common.ps1
Initialize-JpvEnvironment -RuntimeOnly
Push-Location $JpvRoot
try {
    & "$PSScriptRoot/build-native.ps1" -Configuration $Configuration
    Invoke-JpvChecked dotnet @('build','managed/JPVision.Studio.Wpf/JPVision.Studio.Wpf.csproj','-c',$Configuration)
    Invoke-JpvChecked dotnet @('build','samples/WpfSample/WpfSample.csproj','-c',$Configuration)
    Invoke-JpvChecked dotnet @('build','managed/JPVision.Tests/JPVision.Tests.csproj','-c',$Configuration)
    Invoke-JpvChecked dotnet @('build','tools/LicenseIssuer/LicenseIssuer.csproj','-c',$Configuration)
    if ($PublishStudio) {
        Invoke-JpvChecked dotnet @('publish','managed/JPVision.Studio.Wpf/JPVision.Studio.Wpf.csproj','-c',$Configuration,'--self-contained','false','-o',"artifacts/studio-wpf/$Configuration")
    }
} finally { Pop-Location }
