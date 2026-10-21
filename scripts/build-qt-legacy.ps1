param([ValidateSet('Debug','Release')][string]$Configuration='Release')
. $PSScriptRoot\common.ps1
Initialize-JpvEnvironment
Push-Location $JpvRoot
try {
    $suffix=$Configuration.ToLowerInvariant()
    Invoke-JpvChecked $JpvCmake @('--preset','runtime')
    Invoke-JpvChecked $JpvCmake @('--build','--preset',"runtime-$suffix",'--parallel','4')
    Invoke-JpvChecked $JpvCmake @('--preset',"studio-$suffix")
    Invoke-JpvChecked $JpvCmake @('--build','--preset',"studio-$suffix",'--parallel','4')
    # The Qt executable may not relink after a core-only edit; always stage the current runtime.
    Copy-Item -LiteralPath "artifacts/native/$Configuration/JPVision.Runtime.dll" -Destination "artifacts/studio/$Configuration/JPVision.Runtime.dll" -Force
    $opencvName=if($Configuration -eq 'Debug'){'opencv_world500d.dll'}else{'opencv_world500.dll'}
    Copy-Item -LiteralPath "artifacts/native/$Configuration/$opencvName" -Destination "artifacts/studio/$Configuration/$opencvName" -Force
    $qtDeploy="--$suffix"
    if($Configuration -eq 'Debug' -and -not (Test-Path (Join-Path $env:JPV_QT_ROOT 'plugins/platforms/qwindowsd.dll'))){
        $qtDeploy='--release';Write-Output 'This MinGW Qt kit supplies RelWithDebInfo DLLs only; Debug Studio uses those Qt DLLs, with Debug application and Debug MSVC Runtime.'
    }
    Invoke-JpvChecked (Join-Path $env:JPV_QT_ROOT 'bin/windeployqt.exe') @($qtDeploy,'--no-translations','--no-opengl-sw','--compiler-runtime',"artifacts/studio/$Configuration/JP-Vision.exe")
    Invoke-JpvChecked dotnet @('build','samples/WpfSample/WpfSample.csproj','-c',$Configuration)
    Invoke-JpvChecked dotnet @('build','managed/JPVision.Tests/JPVision.Tests.csproj','-c',$Configuration)
    Invoke-JpvChecked dotnet @('build','tools/LicenseIssuer/LicenseIssuer.csproj','-c',$Configuration)
} finally { Pop-Location }
