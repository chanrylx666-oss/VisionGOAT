$ErrorActionPreference = 'Stop'
$JpvRoot = Split-Path $PSScriptRoot -Parent
function Invoke-JpvChecked {
    param([string]$Program, [string[]]$Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}
function Initialize-JpvEnvironment {
    param([switch]$RuntimeOnly)
    if (-not $env:JPV_QT_ROOT) {
        $qt = Get-ChildItem 'C:\Qt\*\mingw_64\lib\cmake\Qt6\Qt6Config.cmake' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if ($qt) { $env:JPV_QT_ROOT = Split-Path (Split-Path (Split-Path (Split-Path $qt.FullName -Parent) -Parent) -Parent) -Parent }
    }
    if (-not $env:JPV_MINGW_ROOT) {
        $compiler = Get-ChildItem 'C:\Qt\Tools\mingw*\bin\g++.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if ($compiler) { $env:JPV_MINGW_ROOT = Split-Path (Split-Path $compiler.FullName -Parent) -Parent }
    }
    if (-not $env:JPV_NINJA) {
        $command = Get-Command ninja -ErrorAction SilentlyContinue
        $env:JPV_NINJA = if ($command) { $command.Source } else { 'C:\Qt\Tools\Ninja\ninja.exe' }
    }
    $cmake = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmake) { $script:JpvCmake = $cmake.Source } else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install CMake or Visual Studio C++ desktop tools.' }
        $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        $script:JpvCmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    }
    $script:JpvCtest = Join-Path (Split-Path $JpvCmake -Parent) 'ctest.exe'
    $required=@($JpvCmake)
    if(-not $RuntimeOnly){$required+=@($env:JPV_NINJA, (Join-Path $env:JPV_QT_ROOT 'bin\windeployqt.exe'), (Join-Path $env:JPV_MINGW_ROOT 'bin\g++.exe'))}
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Required tool missing: $path. Set JPV_QT_ROOT / JPV_MINGW_ROOT / JPV_NINJA as appropriate." }
    }
    if($env:JPV_QT_ROOT){$env:JPV_QT_ROOT = $env:JPV_QT_ROOT.Replace('\', '/')}
    if($env:JPV_MINGW_ROOT){$env:JPV_MINGW_ROOT = $env:JPV_MINGW_ROOT.Replace('\', '/')}
    $env:JPV_NINJA = $env:JPV_NINJA.Replace('\', '/')
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET SDK 10.' }
}
function Invoke-JpvGuiTest {
    param([string]$Executable, [string[]]$Arguments, [string]$Log)
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru -RedirectStandardError $Log
    if (-not $process.WaitForExit(45000)) { Stop-Process -Id $process.Id; throw "GUI test timed out: $Executable" }
    if ($process.ExitCode -ne 0) { throw "GUI test failed ($($process.ExitCode)): $Executable; see $Log" }
}
function Assert-JpvFeaturesEqual {
    param([object[]]$First, [object[]]$Second)
    function Assert-JpvValueEqual($a,$b,[string]$field) {
        if($null -eq $a -or $null -eq $b){if($null -ne $a -or $null -ne $b){throw "Null differs: $field"};return}
        if($a -is [Array]) {
            if($b -isnot [Array] -or $a.Count -ne $b.Count){throw "Array differs: $field"}
            for($i=0;$i -lt $a.Count;++$i){Assert-JpvValueEqual $a[$i] $b[$i] "$field/$i"}
        } elseif($a -is [pscustomobject]) {
            if($b -isnot [pscustomobject] -or @($a.PSObject.Properties).Count -ne @($b.PSObject.Properties).Count){throw "Fields differ: $field"}
            foreach($p in $a.PSObject.Properties){$other=$b.PSObject.Properties[$p.Name];if($null -eq $other){throw "Missing field: $field/$($p.Name)"};Assert-JpvValueEqual $p.Value $other.Value "$field/$($p.Name)"}
        } elseif($a -is [ValueType] -and $a -isnot [bool]) {
            if($b -isnot [ValueType] -or $b -is [bool] -or [Math]::Abs([double]$a-[double]$b) -gt 1e-6){throw "Number differs: $field"}
        } elseif($a -cne $b){throw "Value differs: $field"}
    }
    Assert-JpvValueEqual $First $Second 'Features'
}
