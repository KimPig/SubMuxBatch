[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src\SubMuxBatch.App\SubMuxBatch.App.csproj'
$testPath = Join-Path $projectRoot 'tests\SubMuxBatch.Core.Tests\SubMuxBatch.Core.Tests.csproj'
$outputPath = Join-Path $projectRoot "artifacts\publish\$Runtime"

if (-not $SkipTests) {
    dotnet test $testPath -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Tests failed; publish stopped.'
    }
}

if (Test-Path -LiteralPath $outputPath) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($outputPath)
    $resolvedPublishRoot = ([System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\publish'))).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedOutput.StartsWith($resolvedPublishRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected publish path: $resolvedOutput"
    }

    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

dotnet publish $projectPath `
    -c Release `
    -r $Runtime `
    --self-contained true `
    --nologo `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw 'Publish failed.'
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $outputPath -Force
Write-Host "Published: $outputPath"
Write-Host 'MKVToolNix and seconv are external dependencies and are not bundled.'
