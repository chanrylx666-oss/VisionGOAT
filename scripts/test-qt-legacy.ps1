param([ValidateSet('Debug','Release')][string]$Configuration='Release', [string]$SigningKey=(Join-Path $env:USERPROFILE '.jp-vision\issuer\owner.private'))
. $PSScriptRoot\common.ps1
Initialize-JpvEnvironment
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
    $studio=Join-Path $JpvRoot "artifacts/studio/$Configuration/JP-Vision.exe"
    Invoke-JpvGuiTest $studio @('--smoke-test','--screenshot',(Join-Path $JpvRoot "artifacts/studio-$Configuration.png")) (Join-Path $JpvRoot "artifacts/tests/studio-$Configuration.err")
    Invoke-JpvGuiTest (Join-Path $JpvRoot "artifacts/studio/$Configuration/JPVision.GraphTests.exe") @('-o',"$(Join-Path $JpvRoot "artifacts/tests/graph-$Configuration.txt"),txt") (Join-Path $JpvRoot "artifacts/tests/graph-$Configuration.err")
    foreach($suffix in @('debug-images','no-debug')) {
        $draftArgs=@('--draft-smoke','--screenshot',(Join-Path $JpvRoot "artifacts/draft-$suffix-$Configuration.png"))
        if($suffix -eq 'no-debug'){$draftArgs+='--no-debug'}
        Invoke-JpvGuiTest $studio $draftArgs (Join-Path $JpvRoot "artifacts/tests/draft-$suffix-$Configuration.err")
    }
    $unlicensed=Join-Path $JpvRoot 'artifacts/tests/gui-unlicensed'
    Invoke-JpvGuiTest $studio @('--locked-smoke','--license-store',$unlicensed,'--screenshot',(Join-Path $JpvRoot "artifacts/locked-$Configuration.png")) (Join-Path $JpvRoot "artifacts/tests/locked-$Configuration.err")
    foreach($name in @('wrong-machine','wrong-product','unknown-key','bad-version')){
        $store=Join-Path $JpvRoot "artifacts/tests/gui-$name";New-Item -ItemType Directory -Path $store -Force | Out-Null
        Copy-Item -LiteralPath "artifacts/tests/license-fixtures/$name.vlic" -Destination "$store/license.vlic" -Force
        Invoke-JpvGuiTest $studio @('--locked-smoke','--license-store',$store) (Join-Path $JpvRoot "artifacts/tests/gui-$name-$Configuration.err")
    }
    $wpf=Join-Path $JpvRoot "samples/WpfSample/bin/$Configuration/net10.0-windows/JPVision.WpfSample.exe"
    $directQt=Join-Path $JpvRoot "artifacts/studio-measurement-$Configuration.png"
    $directWpf=Join-Path $JpvRoot "artifacts/wpf-measurement-$Configuration.png"
    Invoke-JpvGuiTest $studio @('--measurement-smoke','--recipe',(Join-Path $JpvRoot 'samples/recipes/circle-measurement.vrecipe'),'--screenshot',$directQt) (Join-Path $JpvRoot "artifacts/tests/studio-measurement-$Configuration.err")
    Invoke-JpvGuiTest $wpf @('--smoke-test','--recipe',(Join-Path $JpvRoot 'samples/recipes/circle-measurement.vrecipe'),'--screenshot',$directWpf) (Join-Path $JpvRoot "artifacts/tests/wpf-measurement-$Configuration.err")
    $directQtResult=Get-Content "$directQt.json" -Raw | ConvertFrom-Json
    $directWpfResult=Get-Content "$directWpf.json" -Raw | ConvertFrom-Json
    if($directQtResult.contentHash -ne $directWpfResult.contentHash -or $directQtResult.outputs.ObjectCount -ne 3 -or $directWpfResult.outputs.ObjectCount -ne 3 -or $directQtResult.decision -ne 'Pass' -or $directWpfResult.decision -ne 'Pass'){throw 'Direct circle measurements differ between Qt and WPF.'}
    Assert-JpvFeaturesEqual @($directQtResult.outputs.Features) @($directWpfResult.outputs.Features)
    $caliperQt=Join-Path $JpvRoot "artifacts/studio-integrated-caliper-$Configuration.png"
    $caliperWpf=Join-Path $JpvRoot "artifacts/wpf-integrated-caliper-$Configuration.png"
    Invoke-JpvGuiTest $studio @('--measurement-smoke','--edit-calipers','--no-debug','--recipe',(Join-Path $JpvRoot 'samples/recipes/circle-caliper-measurement.vrecipe'),'--screenshot',$caliperQt) (Join-Path $JpvRoot "artifacts/tests/studio-integrated-caliper-$Configuration.err")
    Invoke-JpvGuiTest $wpf @('--smoke-test','--recipe',(Join-Path $JpvRoot 'artifacts/editor-built-measurement.vrecipe'),'--screenshot',$caliperWpf) (Join-Path $JpvRoot "artifacts/tests/wpf-integrated-caliper-$Configuration.err")
    $caliperQtResult=Get-Content "$caliperQt.json" -Raw | ConvertFrom-Json
    $caliperWpfResult=Get-Content "$caliperWpf.json" -Raw | ConvertFrom-Json
    if($caliperQtResult.contentHash -ne $caliperWpfResult.contentHash -or $caliperQtResult.decision -ne 'Pass' -or $caliperWpfResult.decision -ne 'Pass' -or $caliperQtResult.outputs.CircleMeasurement.caliper.requestedCount -ne 52 -or $caliperQtResult.outputs.CircleMeasurement.caliper.rejectedCount -lt 6 -or $caliperQtResult.outputs.CircleMeasurement.caliper.rejectedCount -ne $caliperWpfResult.outputs.CircleMeasurement.caliper.rejectedCount){throw 'Edited integrated caliper measurements differ between Qt and WPF.'}
    Assert-JpvFeaturesEqual @($caliperQtResult.outputs.Features) @($caliperWpfResult.outputs.Features)
    $processingRecipe=Join-Path $JpvRoot 'samples/recipes/grayscale.vrecipe'
    $processingImage=Join-Path $JpvRoot 'artifacts/draft-processing-input.png'
    $qtProcessing=Join-Path $JpvRoot "artifacts/studio-processing-$Configuration.png"
    $wpfProcessing=Join-Path $JpvRoot "artifacts/wpf-processing-$Configuration.png"
    Invoke-JpvGuiTest $studio @('--processing-smoke','--recipe',$processingRecipe,'--image',$processingImage,'--screenshot',$qtProcessing) (Join-Path $JpvRoot "artifacts/tests/studio-processing-$Configuration.err")
    Invoke-JpvGuiTest $wpf @('--processing-smoke','--recipe',$processingRecipe,'--image',$processingImage,'--screenshot',$wpfProcessing) (Join-Path $JpvRoot "artifacts/tests/wpf-processing-$Configuration.err")
    $qtProcessingResult=Get-Content "$qtProcessing.json" -Raw | ConvertFrom-Json
    $wpfProcessingResult=Get-Content "$wpfProcessing.json" -Raw | ConvertFrom-Json
    if($qtProcessingResult.contentHash -ne $wpfProcessingResult.contentHash -or $qtProcessingResult.decision -ne 'Unknown' -or $wpfProcessingResult.decision -ne 'Unknown' -or $qtProcessingResult.outputs.GrayImage.pixelFormat -ne $wpfProcessingResult.outputs.GrayImage.pixelFormat){throw 'Qt/WPF processing results differ.'}
    Invoke-JpvGuiTest $wpf @('--smoke-test','--recipe',(Join-Path $JpvRoot "artifacts/editor-built-studio-$Configuration.vrecipe"),'--screenshot',(Join-Path $JpvRoot "artifacts/wpf-$Configuration.png")) (Join-Path $JpvRoot "artifacts/tests/wpf-$Configuration.err")
    $qtResult=Get-Content "artifacts/studio-$Configuration.png.json" -Raw | ConvertFrom-Json
    $wpfResult=Get-Content "artifacts/wpf-$Configuration.png.json" -Raw | ConvertFrom-Json
    if($qtResult.contentHash -ne $wpfResult.contentHash -or $qtResult.decision -ne $wpfResult.decision -or $qtResult.outputs.ObjectCount -ne $wpfResult.outputs.ObjectCount){throw 'Qt-produced recipe and WPF results differ.'}
    $qtRoiCapture=Join-Path $JpvRoot "artifacts/studio-roi-$Configuration.png"
    $wpfRoiCapture=Join-Path $JpvRoot "artifacts/wpf-roi-$Configuration.png"
    Invoke-JpvGuiTest $studio @('--ui-smoke','--recipe',(Join-Path $JpvRoot 'samples/recipes/circles.vrecipe'),'--screenshot',$qtRoiCapture) (Join-Path $JpvRoot "artifacts/tests/studio-roi-$Configuration.err")
    Invoke-JpvGuiTest $wpf @('--smoke-test','--recipe',(Join-Path $JpvRoot 'artifacts/editor-built-ui-roi.vrecipe'),'--screenshot',$wpfRoiCapture) (Join-Path $JpvRoot "artifacts/tests/wpf-roi-$Configuration.err")
    $qtRoi=Get-Content "$qtRoiCapture.json" -Raw | ConvertFrom-Json
    $wpfRoi=Get-Content "$wpfRoiCapture.json" -Raw | ConvertFrom-Json
    if($qtRoi.contentHash -ne $wpfRoi.contentHash -or $qtRoi.outputs.ObjectCount -ne 2 -or $wpfRoi.outputs.ObjectCount -ne 2 -or $qtRoi.decision -ne 'Pass' -or $wpfRoi.decision -ne 'Pass'){throw 'Editable ROI recipe and WPF results differ.'}
    Assert-JpvFeaturesEqual @($qtRoi.outputs.Features) @($wpfRoi.outputs.Features)
    Invoke-JpvGuiTest $studio @('--ui-smoke','--no-debug','--recipe',(Join-Path $JpvRoot 'samples/recipes/circles.vrecipe'),'--screenshot',(Join-Path $JpvRoot "artifacts/studio-roi-no-debug-$Configuration.png")) (Join-Path $JpvRoot "artifacts/tests/studio-roi-no-debug-$Configuration.err")
    foreach($name in @('circles','lines','regions','line-caliper','circle-caliper','template-match')){
        $recipe=Join-Path $JpvRoot "samples/recipes/$name.vrecipe"
        $imageName=if($name -eq 'lines'){'three-lines'}elseif($name -eq 'line-caliper'){'single-edge'}else{'three-parts'}
        $image=Join-Path $JpvRoot "samples/images/$imageName.png"
        $qtCapture=Join-Path $JpvRoot "artifacts/studio-$name-$Configuration.png"
        $wpfCapture=Join-Path $JpvRoot "artifacts/wpf-$name-$Configuration.png"
        Invoke-JpvGuiTest $studio @('--sample-smoke','--recipe',$recipe,'--image',$image,'--screenshot',$qtCapture) (Join-Path $JpvRoot "artifacts/tests/studio-$name-$Configuration.err")
        Invoke-JpvGuiTest $wpf @('--smoke-test','--recipe',$recipe,'--image',$image,'--screenshot',$wpfCapture) (Join-Path $JpvRoot "artifacts/tests/wpf-$name-$Configuration.err")
        $qtGeometry=Get-Content "$qtCapture.json" -Raw | ConvertFrom-Json
        $wpfGeometry=Get-Content "$wpfCapture.json" -Raw | ConvertFrom-Json
        if($qtGeometry.contentHash -ne $wpfGeometry.contentHash -or $qtGeometry.decision -ne 'Pass' -or $wpfGeometry.decision -ne 'Pass'){
            throw "Qt/WPF geometry mismatch: $name"
        }
        Assert-JpvFeaturesEqual @($qtGeometry.outputs.Features) @($wpfGeometry.outputs.Features)
    }
    $templateCapture=Join-Path $JpvRoot "artifacts/studio-taught-template-$Configuration.png"
    $templateWpfCapture=Join-Path $JpvRoot "artifacts/wpf-taught-template-$Configuration.png"
    Invoke-JpvGuiTest $studio @('--template-smoke','--recipe',(Join-Path $JpvRoot 'samples/recipes/template-match.vrecipe'),'--screenshot',$templateCapture) (Join-Path $JpvRoot "artifacts/tests/studio-taught-template-$Configuration.err")
    Invoke-JpvGuiTest $wpf @('--smoke-test','--recipe',(Join-Path $JpvRoot 'artifacts/editor-built-template.vrecipe'),'--screenshot',$templateWpfCapture) (Join-Path $JpvRoot "artifacts/tests/wpf-taught-template-$Configuration.err")
    $qtTemplate=Get-Content "$templateCapture.json" -Raw | ConvertFrom-Json
    $wpfTemplate=Get-Content "$templateWpfCapture.json" -Raw | ConvertFrom-Json
    if($qtTemplate.contentHash -ne $wpfTemplate.contentHash -or $qtTemplate.decision -ne 'Pass' -or $wpfTemplate.decision -ne 'Pass'){throw 'Taught template recipe differs in WPF.'}
    Assert-JpvFeaturesEqual @($qtTemplate.outputs.Features) @($wpfTemplate.outputs.Features)
    $maskedTemplateCapture=Join-Path $JpvRoot "artifacts/studio-taught-masked-template-$Configuration.png"
    $maskedWpfCapture=Join-Path $JpvRoot "artifacts/wpf-taught-masked-template-$Configuration.png"
    Invoke-JpvGuiTest $studio @('--template-smoke','--source-roi','--recipe',(Join-Path $JpvRoot 'samples/recipes/template-match.vrecipe'),'--screenshot',$maskedTemplateCapture) (Join-Path $JpvRoot "artifacts/tests/studio-taught-masked-template-$Configuration.err")
    Invoke-JpvGuiTest $wpf @('--smoke-test','--recipe',(Join-Path $JpvRoot 'artifacts/editor-built-template.vrecipe'),'--screenshot',$maskedWpfCapture) (Join-Path $JpvRoot "artifacts/tests/wpf-taught-masked-template-$Configuration.err")
    $qtMaskedTemplate=Get-Content "$maskedTemplateCapture.json" -Raw | ConvertFrom-Json
    $wpfMaskedTemplate=Get-Content "$maskedWpfCapture.json" -Raw | ConvertFrom-Json
    if($qtMaskedTemplate.contentHash -ne $wpfMaskedTemplate.contentHash -or $qtMaskedTemplate.decision -ne 'Pass' -or $wpfMaskedTemplate.decision -ne 'Pass' -or $qtMaskedTemplate.outputs.ObjectCount -ne 1 -or $wpfMaskedTemplate.outputs.ObjectCount -ne 1){throw 'Template learning in upstream ROI differs in WPF.'}
    Assert-JpvFeaturesEqual @($qtMaskedTemplate.outputs.Features) @($wpfMaskedTemplate.outputs.Features)
    Copy-Item -LiteralPath 'artifacts/tests/performance.json' -Destination "artifacts/tests/performance-$Configuration.json" -Force
    Write-Output 'Native, SDK, editor interactions, editable ROI, unauthorized editor and WPF GUI tests passed.'
} finally { Pop-Location }
