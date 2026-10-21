. $PSScriptRoot\common.ps1
Push-Location $JpvRoot
try {
    $template=Get-Content 'samples/recipes/template-match.vrecipe/recipe.json' -Raw | ConvertFrom-Json
    $model=($template.nodes | Where-Object type -eq 'TemplateMatch').parameters.model
    foreach($single in @($false,$true)) {
        $name=if($single){'fast-locate'}else{'fast-match'}
        $type=if($single){'FastLocate'}else{'FastTemplateMatch'}
        $count=if($single){1}else{3}
        $definition=[ordered]@{
            schemaVersion=1;minimumRuntimeAbi=1;recipeId="jp-$name";recipeVersion=1;kind='inspection';decisionOutput='InspectionPassed'
            inputs=@{InputImage=@{type='Image';required=$true}}
            nodes=@(@{id='source';type='ExternalImage';version=1;parameters=@{input='InputImage'}},@{id='match';type=$type;version=1;parameters=@{model=$model;scoreThreshold=.8;minimumCount=$count;maximumCount=$count}})
            connections=@(@{fromNode='source';fromPort='image';toNode='match';toPort='input'})
            outputs=@{Features=@{node='match';port='features';type='Features'};MatchMeasurement=@{node='match';port='measurement';type='Measurement'};ObjectCount=@{node='match';port='count';type='Int'};InspectionPassed=@{node='match';port='passed';type='Bool'};ResultOverlay=@{node='match';port='overlay';type='Overlay'}}
            resources=@()
        }
        $folder="samples/recipes/$name.vrecipe";New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $temporary=Join-Path $JpvRoot "artifacts/$name-unsealed.json"
        $definition | ConvertTo-Json -Depth 30 -Compress | Set-Content -LiteralPath $temporary -Encoding utf8
        Invoke-JpvChecked (Join-Path $JpvRoot 'artifacts/native/Release/JPVision.RuntimeCli.exe') @('seal',$temporary,(Join-Path $JpvRoot "$folder/recipe.json"))
        @{positions=@{source=@{x=40;y=60};match=@{x=300;y=60}}} | ConvertTo-Json -Depth 5 | Set-Content "$folder/layout.json" -Encoding utf8
    }
} finally { Pop-Location }
