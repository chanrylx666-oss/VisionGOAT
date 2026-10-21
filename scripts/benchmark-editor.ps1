param([ValidateRange(20, 1000)][int]$Iterations = 20, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$benchmarkRepository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $benchmarkRepository ('artifacts/tests/editor-performance/' + [guid]::NewGuid().ToString('N')) }
$benchmarkOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $benchmarkOutput -Force | Out-Null
$benchmarkArtifacts = Join-Path $benchmarkOutput 'managed-output'
$benchmarkProject = Join-Path $benchmarkRepository 'tools/EditorBenchmark/EditorBenchmark.csproj'
& dotnet build $benchmarkProject -c Debug -p:UseArtifactsOutput=true "-p:ArtifactsPath=$benchmarkArtifacts" --nologo --verbosity quiet *> (Join-Path $benchmarkOutput 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Benchmark build failed; see $benchmarkOutput/build.log" }
$benchmarkAssembly = Join-Path $benchmarkArtifacts 'bin/EditorBenchmark/debug/EditorBenchmark.dll'
& dotnet $benchmarkAssembly $benchmarkRepository $benchmarkOutput $Iterations *> (Join-Path $benchmarkOutput 'run.log')
if ($LASTEXITCODE -ne 0) { throw "Benchmark failed; see $benchmarkOutput/run.log" }
Get-Content -LiteralPath (Join-Path $benchmarkOutput 'run.log')
Write-Output "Report: $benchmarkOutput/editor-performance.json"
