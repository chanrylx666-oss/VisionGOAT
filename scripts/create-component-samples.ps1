. $PSScriptRoot\common.ps1
Push-Location $JpvRoot
try {
    $cli = Join-Path $JpvRoot 'artifacts/native/Release/JPVision.RuntimeCli.exe'
    Invoke-JpvChecked (Join-Path $JpvRoot 'artifacts/native/Release/JPVision.Probe.exe') @('samples/images')
    function New-Node([string]$Id, [string]$Type, [hashtable]$Parameters = @{}) {
        @{id=$Id;type=$Type;version=1;parameters=$Parameters}
    }
    function New-Edge([string]$From, [string]$Port, [string]$To) {
        @{fromNode=$From;fromPort=$Port;toNode=$To;toPort='input'}
    }
    foreach ($name in @('circles','lines','regions','line-caliper','circle-caliper','template-match')) {
        $nodes = [Collections.Generic.List[object]]::new()
        $edges = [Collections.Generic.List[object]]::new()
        $nodes.Add((New-Node 'source' 'ExternalImage' @{input='InputImage'}))
        if ($name -eq 'regions') {
            $nodes.Add((New-Node 'gray' 'ToGray'))
            $nodes.Add((New-Node 'threshold' 'Threshold' @{value=128;invert=$false}))
            $nodes.Add((New-Node 'morph' 'Morphology' @{operation='open';kernel=3;iterations=1}))
            $nodes.Add((New-Node 'contours' 'FindContours'))
            $nodes.Add((New-Node 'detect' 'AnalyzeContours'))
            $edges.Add((New-Edge 'source' 'image' 'gray'))
            $edges.Add((New-Edge 'gray' 'image' 'threshold'))
            $edges.Add((New-Edge 'threshold' 'mask' 'morph'))
            $edges.Add((New-Edge 'morph' 'mask' 'contours'))
            $edges.Add((New-Edge 'contours' 'contours' 'detect'))
        } elseif ($name -eq 'circles') {
            $nodes.Add((New-Node 'detect' 'FindCircles' @{minimumRadius=35;maximumRadius=60;minimumDistance=100;voteThreshold=30}))
            $edges.Add((New-Edge 'source' 'image' 'detect'))
        } elseif ($name -eq 'lines') {
            $nodes.Add((New-Node 'detect' 'FindLines' @{minimumLength=200;voteThreshold=100;maximumGap=10}))
            $edges.Add((New-Edge 'source' 'image' 'detect'))
        } elseif ($name -eq 'line-caliper') {
            $nodes.Add((New-Node 'detect' 'LineCaliper' @{roi=@{shape='rectangle';x=80;y=140;width=480;height=80};searchLength=60;polarity='darkToLight'}))
            $edges.Add((New-Edge 'source' 'image' 'detect'))
        } elseif ($name -eq 'circle-caliper') {
            $nodes.Add((New-Node 'detect' 'CircleCaliper' @{roi=@{shape='circle';cx=120;cy=180;radius=70};radius=48;searchLength=30;polarity='lightToDark';caliperCount=36}))
            $edges.Add((New-Edge 'source' 'image' 'detect'))
        } else {
            $templatePixels=[byte[]]::new(110*110)
            for($y=0;$y -lt 110;++$y){for($x=0;$x -lt 110;++$x){$templatePixels[$y*110+$x]=if(($x-55)*($x-55)+($y-55)*($y-55) -le 48*48){235}else{18}}}
            $nodes.Add((New-Node 'detect' 'TemplateMatch' @{model=@{width=110;height=110;grayBase64=[Convert]::ToBase64String($templatePixels)};scoreThreshold=0.95;maximumMatches=5}))
            $edges.Add((New-Edge 'source' 'image' 'detect'))
        }
        $nodes.Add((New-Node 'count' 'CountFeatures'))
        $nodes.Add((New-Node 'judge' 'CompareNumber' @{expected=@{input='ExpectedCount'}}))
        $nodes.Add((New-Node 'overlay' 'BuildFeatureOverlay' @{color='#44D7B6';lineWidth=2}))
        $edges.Add((New-Edge 'detect' 'features' 'count'))
        $edges.Add((New-Edge 'count' 'value' 'judge'))
        $edges.Add((New-Edge 'detect' 'features' 'overlay'))
        $expected = if ($name -eq 'lines') {6} elseif ($name -like '*caliper') {1} else {3}
        $recipe = @{
            schemaVersion=1;minimumRuntimeAbi=1;kind='inspection';recipeId="jp-$name";recipeVersion=1
            nodes=$nodes.ToArray();connections=$edges.ToArray();resources=@();decisionOutput='InspectionPassed'
            inputs=@{InputImage=@{type='Image';required=$true};ExpectedCount=@{type='Int';default=$expected}}
            outputs=@{
                Features=@{node='detect';port='features';type='Features'}
                ObjectCount=@{node='count';port='value';type='Int';unit='count'}
                InspectionPassed=@{node='judge';port='value';type='Bool'}
                ResultOverlay=@{node='overlay';port='overlay';type='Overlay'}
            }
        }
        $path = Join-Path $JpvRoot "samples/recipes/$name.vrecipe"
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        $json = Join-Path $path 'recipe.json'
        [IO.File]::WriteAllText($json, ($recipe | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
        Invoke-JpvChecked $cli @('seal',$json,$json)
        $positions=@{}; $column=0
        foreach ($node in $nodes) {
            if ($node.id -eq 'overlay') {$positions[$node.id]=@{x=($column-3)*260;y=270}}
            else {$positions[$node.id]=@{x=$column*260;y=50};++$column}
        }
        [IO.File]::WriteAllText((Join-Path $path 'layout.json'), (@{positions=$positions} | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    }
    Write-Output 'Circle, line, region, caliper and template sample recipes generated and sealed.'
} finally { Pop-Location }
