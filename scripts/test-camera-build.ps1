param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$testsRoot = Join-Path $projectRoot 'artifacts/tests'
$runRoot = Join-Path $testsRoot ('camera-copy-regression/' + [Guid]::NewGuid().ToString('N'))
$reportPath = Join-Path $testsRoot 'camera-copy-regression.json'
$null = New-Item -ItemType Directory -Path $runRoot -Force
$fixtureRoot = Join-Path $runRoot 'native-fixture'
$consumerRoot = Join-Path $runRoot 'consumer'
$null = New-Item -ItemType Directory -Path $consumerRoot -Force
$consumerProject = Join-Path $consumerRoot 'CameraCopyConsumer.csproj'
$lateTargets = Join-Path $runRoot 'late-generator.targets'
$evaluationEvidence = Join-Path $runRoot 'late-evaluation.txt'
$firstArtifacts = Join-Path $runRoot 'build-late'
$missingArtifacts = Join-Path $runRoot 'build-missing'
$missingNativeRoot = Join-Path $runRoot 'native-never-created'
$cases = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    startedAt = [DateTimeOffset]::Now.ToString('o')
    configuration = $Configuration
    scope = 'Isolated managed asset-copy regression; text fixtures are never loaded as native libraries.'
    runRoot = $runRoot
    cases = $cases
    succeeded = $false
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-PrivatePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($runRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a write or deletion outside the private test directory: $full"
    }
    return $full
}

function Remove-PrivateFile([string]$Path) {
    $checkedPath = Assert-PrivatePath $Path
    if (Test-Path -LiteralPath $checkedPath) {
        Assert-Condition (Test-Path -LiteralPath $checkedPath -PathType Leaf) 'Only individual private files may be removed.'
        Remove-Item -LiteralPath $checkedPath -Force
    }
}

function Invoke-IsolatedBuild([string]$Name, [string]$Artifacts, [string]$NativeDirectory, [bool]$GenerateFixture) {
    $null = Assert-PrivatePath $Artifacts
    $null = Assert-PrivatePath $NativeDirectory
    $log = Join-Path $runRoot ($Name + '.log')
    $arguments = @(
        'build', $consumerProject, '--nologo', '-c', $Configuration, '-v:minimal',
        '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$Artifacts",
        "-p:JpvCameraNativeDirectory=$NativeDirectory",
        "-p:CustomAfterMicrosoftCommonTargets=$lateTargets",
        "-p:JpvGenerateCameraFixture=$($GenerateFixture.ToString().ToLowerInvariant())",
        "-p:JpvFixtureEvidenceFile=$evaluationEvidence"
    )
    & dotnet @arguments *> $log
    if ($LASTEXITCODE -ne 0) { throw "Isolated build $Name failed ($LASTEXITCODE); see $log" }
    return $log
}

function Get-ConsumerDirectory([string]$Artifacts) {
    $bin = Join-Path $Artifacts 'bin/CameraCopyConsumer'
    $assemblies = @(Get-ChildItem -LiteralPath $bin -Filter 'CameraCopyConsumer.dll' -File -Recurse)
    Assert-Condition ($assemblies.Count -eq 1) "Expected one isolated consumer assembly under $bin."
    return $assemblies[0].DirectoryName
}

function Assert-FixturesCopied([string]$Directory) {
    foreach ($name in @('JPVision.Camera.Mvs.dll', 'JPVision.Camera.Mvs.pdb')) {
        $source = Join-Path $fixtureRoot $name
        $target = Join-Path $Directory $name
        Assert-Condition (Test-Path -LiteralPath $target -PathType Leaf) "Missing transitive native asset: $target"
        Assert-Condition ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) "Native asset hash differs: $target"
    }
}

try {
    $acquisitionProject = [Security.SecurityElement]::Escape((Join-Path $projectRoot 'managed/JPVision.Acquisition/JPVision.Acquisition.csproj'))
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$acquisitionProject" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $consumerProject -Encoding utf8
    @'
using JPVision.Acquisition;
using JPVision.Sdk;
using System.Diagnostics;
using System.Text.Json;

// This host has an acquisition project reference, but file mode never calls a camera API.
_ = typeof(MvsCameraProvider);
byte[] pixels = File.ReadAllBytes(args[0]);
using var frame = VisionFrame.CopyFrom(pixels, 2, 2, 2, VisionPixelFormat.Gray8);
if (!frame.CopyPixels().SequenceEqual(pixels)) throw new InvalidOperationException("File frame pixels changed.");
bool bridgeLoaded = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
    .Any(module => module.ModuleName.Equals("JPVision.Camera.Mvs.dll", StringComparison.OrdinalIgnoreCase));
if (bridgeLoaded) throw new InvalidOperationException("File source unexpectedly loaded the camera bridge.");
Console.WriteLine(JsonSerializer.Serialize(new { fileFrameCopied = true, cameraBridgeLoaded = bridgeLoaded, width = frame.Info.Width, height = frame.Info.Height }));
'@ | Set-Content -LiteralPath (Join-Path $consumerRoot 'Program.cs') -Encoding utf8
    @'
<Project>
  <PropertyGroup Condition="'$(MSBuildProjectName)' == 'JPVision.Acquisition'">
    <JpvFixtureMissingAtEvaluation Condition="!Exists('$(JpvCameraNativeDirectory)/JPVision.Camera.Mvs.dll')">true</JpvFixtureMissingAtEvaluation>
  </PropertyGroup>
  <Target Name="GenerateLateCameraCopyFixture" BeforeTargets="JpvCollectCameraNativeArtifacts"
          Condition="'$(MSBuildProjectName)' == 'JPVision.Acquisition' and '$(JpvGenerateCameraFixture)' == 'true'">
    <MakeDir Directories="$(JpvCameraNativeDirectory)" />
    <WriteLinesToFile File="$(JpvCameraNativeDirectory)/JPVision.Camera.Mvs.dll" Lines="copy-regression-dll-revision-1-NOT-A-NATIVE-LIBRARY" Overwrite="true" />
    <WriteLinesToFile File="$(JpvCameraNativeDirectory)/JPVision.Camera.Mvs.pdb" Lines="copy-regression-pdb-revision-1-NOT-A-SYMBOL-FILE" Overwrite="true" />
    <WriteLinesToFile File="$(JpvFixtureEvidenceFile)" Lines="missingAtEvaluation=$(JpvFixtureMissingAtEvaluation)" Overwrite="true" />
    <Message Importance="high" Text="Generated camera text fixtures at execution time; missingAtEvaluation=$(JpvFixtureMissingAtEvaluation)" />
  </Target>
</Project>
'@ | Set-Content -LiteralPath $lateTargets -Encoding utf8

    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $fixtureRoot 'JPVision.Camera.Mvs.dll'))) 'The first fixture must be missing before project evaluation.'
    $log = Invoke-IsolatedBuild '01-late-generation' $firstArtifacts $fixtureRoot $true
    Assert-Condition ((Get-Content -LiteralPath $evaluationEvidence -Raw).Trim() -eq 'missingAtEvaluation=true') 'Fixture was already present at evaluation; this did not exercise late generation.'
    $consumerDirectory = Get-ConsumerDirectory $firstArtifacts
    Assert-FixturesCopied $consumerDirectory
    $cases.Add([ordered]@{ name = 'late-generation-transitive-copy'; passed = $true; missingAtEvaluation = $true; output = $consumerDirectory; dllSha256 = (Get-FileHash -LiteralPath (Join-Path $consumerDirectory 'JPVision.Camera.Mvs.dll')).Hash; log = $log })

    $consumerDll = Join-Path $consumerDirectory 'JPVision.Camera.Mvs.dll'
    Remove-PrivateFile $consumerDll
    Assert-Condition (-not (Test-Path -LiteralPath $consumerDll)) 'The isolated consumer DLL was not removed.'
    $log = Invoke-IsolatedBuild '02-missing-destination' $firstArtifacts $fixtureRoot $false
    Assert-FixturesCopied $consumerDirectory
    $cases.Add([ordered]@{ name = 'incremental-missing-destination-restored'; passed = $true; dllSha256 = (Get-FileHash -LiteralPath $consumerDll).Hash; log = $log })

    $beforeHash = (Get-FileHash -LiteralPath $consumerDll).Hash
    foreach ($name in @('JPVision.Camera.Mvs.dll', 'JPVision.Camera.Mvs.pdb')) {
        $source = Assert-PrivatePath (Join-Path $fixtureRoot $name)
        [IO.File]::WriteAllText($source, "copy-regression-revision-2-$name-NOT-A-NATIVE-LIBRARY")
        # PreserveNewest compares timestamps; make the updated fixture deterministically newer.
        [IO.File]::SetLastWriteTimeUtc($source, [IO.File]::GetLastWriteTimeUtc((Join-Path $consumerDirectory $name)).AddSeconds(2))
    }
    $log = Invoke-IsolatedBuild '03-updated-source' $firstArtifacts $fixtureRoot $false
    Assert-FixturesCopied $consumerDirectory
    $afterHash = (Get-FileHash -LiteralPath $consumerDll).Hash
    Assert-Condition ($beforeHash -ne $afterHash) 'Incremental build kept the previous fixture content.'
    $cases.Add([ordered]@{ name = 'incremental-source-update-propagated'; passed = $true; previousDllSha256 = $beforeHash; dllSha256 = $afterHash; log = $log })

    Assert-Condition (-not (Test-Path -LiteralPath $missingNativeRoot)) 'The absent-native case must start with no fixture directory.'
    $log = Invoke-IsolatedBuild '04-optional-native-absent' $missingArtifacts $missingNativeRoot $false
    $missingConsumer = Get-ConsumerDirectory $missingArtifacts
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $missingConsumer 'JPVision.Camera.Mvs.dll'))) 'Absent-native build copied a stale camera DLL.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $missingConsumer 'JPVision.Camera.Mvs.pdb'))) 'Absent-native build copied stale symbols.'
    $pixelsPath = Join-Path $runRoot 'file-source-pixels.bin'
    [IO.File]::WriteAllBytes($pixelsPath, [byte[]]@(0, 64, 128, 255))
    $runLog = Join-Path $runRoot '04-file-source-run.log'
    & dotnet (Join-Path $missingConsumer 'CameraCopyConsumer.dll') $pixelsPath *> $runLog
    if ($LASTEXITCODE -ne 0) { throw "Isolated file-frame consumer failed ($LASTEXITCODE); see $runLog" }
    $fileRun = Get-Content -LiteralPath $runLog -Raw | ConvertFrom-Json
    Assert-Condition ($fileRun.fileFrameCopied -and -not $fileRun.cameraBridgeLoaded) 'File frame host did not remain independent of the camera bridge.'
    $cases.Add([ordered]@{ name = 'optional-native-absent-file-frame-works'; passed = $true; nativeDirectoryAbsent = -not (Test-Path -LiteralPath $missingNativeRoot); output = $missingConsumer; fileFrameCopied = $fileRun.fileFrameCopied; cameraBridgeLoaded = $fileRun.cameraBridgeLoaded; buildLog = $log; runLog = $runLog })

    # Ensure recorded build outputs all remained in the private ArtifactsPath trees.
    $fileLists = @(Get-ChildItem -LiteralPath $firstArtifacts, $missingArtifacts -Filter '*.FileListAbsolute.txt' -File -Recurse)
    foreach ($fileList in $fileLists) {
        foreach ($writtenPath in (Get-Content -LiteralPath $fileList.FullName)) {
            if ($writtenPath.Trim()) { $null = Assert-PrivatePath $writtenPath.Trim() }
        }
    }
    $report['isolatedFileListsChecked'] = $fileLists.Count
    $report['succeeded'] = $true
}
catch {
    $report['error'] = $_.Exception.Message
    throw
}
finally {
    $report['finishedAt'] = [DateTimeOffset]::Now.ToString('o')
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "Camera build copy regression: $($cases.Count)/4 cases; succeeded=$($report.succeeded); report=$reportPath"
}
