param([ValidateSet('Debug','Release')][string]$Configuration='Release', [string]$SigningKey=(Join-Path $env:USERPROFILE '.jp-vision\issuer\owner.private'))
. $PSScriptRoot\common.ps1
Initialize-JpvEnvironment -RuntimeOnly
Push-Location $JpvRoot
try {
    if(-not (Test-Path 'artifacts/activation/current.vlic')){throw 'First sign a development license with scripts/activate-dev.ps1. No unlicensed bypass is used.'}
    New-Item -ItemType Directory -Path 'artifacts/tests' -Force | Out-Null
    Invoke-JpvChecked dotnet @('tools/LicenseIssuer/bin/Release/net10.0-windows/JPVision.LicenseIssuer.dll','test-fixtures','--key',$SigningKey,'--request','artifacts/activation/current.vreq','--out','artifacts/tests/license-fixtures')
    Invoke-JpvChecked $JpvCtest @('--preset',"runtime-$($Configuration.ToLowerInvariant())")
    $cli=Join-Path $JpvRoot "artifacts/native/$Configuration/JPVision.RuntimeCli.exe"
    $shared=Join-Path $JpvRoot "artifacts/tests/multiprocess-$Configuration"
    New-Item -ItemType Directory -Path $shared -Force | Out-Null
    $imports=0..3 | ForEach-Object {Start-Process -FilePath $cli -ArgumentList 'import',(Join-Path $JpvRoot 'artifacts/activation/current.vlic'),$shared -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $JpvRoot "artifacts/tests/import-$Configuration-$_.out") -RedirectStandardError (Join-Path $JpvRoot "artifacts/tests/import-$Configuration-$_.err")}
    foreach($process in $imports){if(-not $process.WaitForExit(15000)){Stop-Process -Id $process.Id;throw 'Multi-process import timed out.'};if($process.ExitCode -ne 0){throw 'Multi-process import failed.'}}
    $licenseState=& $cli status $shared | ConvertFrom-Json;if($LASTEXITCODE -ne 0 -or $licenseState.code -ne 0){throw 'Multi-process import corrupted the persisted license.'}
    Invoke-JpvChecked dotnet @('test','managed/JPVision.Tests/JPVision.Tests.csproj','-c',$Configuration,'--logger',"trx;LogFileName=sdk-$Configuration.trx",'--results-directory','artifacts/tests')
    $studio=Join-Path $JpvRoot "managed/JPVision.Studio.Wpf/bin/$Configuration/net10.0-windows/JP-Vision.exe"
    foreach($case in @(
        'recipe-open-dialog','direct-results','single-result','match-circle','match-center-circle','fast-full-image',
        'fast-teaching','fast-teaching-circle','fast-teaching-polygon','caliper','large-image',
        'caliper-boundary-circle','caliper-boundary-rectangle','caliper-boundary-polygon',
        'geometry-distance','fixture-circle-distance','roi-annulus','roi-sector','roi-rotated',
        'edgepair-binding','caliper-result-annulus','large-inner-circle','caliper-refit','caliper-fit-failure',
        'line-strip','line-strip-roi','line-strip-empty','line-distance','line-distance-angle','line-distance-empty',
        'circle-distance-center','circle-distance-edge','circle-distance-empty','line-circle-center','line-circle-edge','line-circle-empty',
        'parameters-threshold','parameters-gaussian','parameters-morphology','parameters-caliper',
        'image-inline-circles','image-inline-lines','image-process-chain','fixture-image-distance','annular-unwrap','parameter-subscriptions','edge-points','edge-reconstruction','roi-bounds','edge-points-ranges','line-density','blob-analysis','camera-source','camera-main-live')) {
        $capture=Join-Path $JpvRoot "artifacts/wpf-editor-$case-$Configuration.png"
        Invoke-JpvGuiTest $studio @('--smoke-test','--case',$case,'--capture',$capture) (Join-Path $JpvRoot "artifacts/tests/wpf-editor-$case-$Configuration.err")
        $record=Get-Content "$capture.json" -Raw | ConvertFrom-Json
        if(-not $record.licensed -or $record.executionStatus -ne 'Succeeded'){throw "WPF editor smoke failed: $case"}
    }
    $emptyStore=Join-Path $JpvRoot "artifacts/tests/wpf-empty-$Configuration-$(Get-Date -Format yyyyMMddHHmmss)"
    $lockedCapture=Join-Path $JpvRoot "artifacts/wpf-editor-locked-$Configuration.png"
    Invoke-JpvGuiTest $studio @('--smoke-test','--case','license-lock','--license-store',$emptyStore,'--capture',$lockedCapture) (Join-Path $JpvRoot "artifacts/tests/wpf-editor-locked-$Configuration.err")
    foreach($name in @('wrong-machine','wrong-product','unknown-key','bad-version','no-studio')) {
        $store=Join-Path $JpvRoot "artifacts/tests/wpf-$name-$Configuration"
        New-Item -ItemType Directory -Path $store -Force | Out-Null
        Copy-Item -LiteralPath "artifacts/tests/license-fixtures/$name.vlic" -Destination "$store/license.vlic" -Force
        $capture=Join-Path $JpvRoot "artifacts/wpf-editor-$name-$Configuration.png"
        Invoke-JpvGuiTest $studio @('--smoke-test','--case','license-lock','--license-store',$store,'--capture',$capture) (Join-Path $JpvRoot "artifacts/tests/wpf-editor-$name-$Configuration.err")
    }
    $sample=Join-Path $JpvRoot "samples/WpfSample/bin/$Configuration/net10.0-windows/JPVision.WpfSample.exe"
    foreach($name in @('parts-count','circle-measurement','circle-caliper-measurement','template-match')) {
        $recipe=Join-Path $JpvRoot "samples/recipes/$name.vrecipe"
        Invoke-JpvGuiTest $sample @('--smoke-test','--recipe',$recipe,'--screenshot',(Join-Path $JpvRoot "artifacts/wpf-sample-$name-$Configuration.png")) (Join-Path $JpvRoot "artifacts/tests/wpf-sample-$name-$Configuration.err")
    }
    Copy-Item -LiteralPath 'artifacts/tests/performance.json' -Destination "artifacts/tests/performance-$Configuration.json" -Force
    Write-Output 'Native, SDK, WPF editor workflows, saved recipes, license negatives and WPF integration sample passed.'
} finally { Pop-Location }
