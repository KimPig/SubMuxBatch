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
$version = Get-Date -Format 'yyyy.MM.dd'
$assemblyVersion = Get-Date -Format 'yyyy.M.d.0'
$releasePath = Join-Path $projectRoot "artifacts\release\v$version"
$releaseArchivePath = Join-Path $releasePath "SubMuxBatch-v$version-$Runtime.zip"

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
    -p:Version=$version `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:InformationalVersion=$version `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw 'Publish failed.'
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $outputPath -Force
[System.IO.Directory]::CreateDirectory($releasePath) | Out-Null
if (Test-Path -LiteralPath $releaseArchivePath) {
    Remove-Item -LiteralPath $releaseArchivePath -Force
}
Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $releaseArchivePath -CompressionLevel Optimal
Write-Host "Published: $outputPath"
Write-Host "Release archive: $releaseArchivePath"
Write-Host 'MKVToolNix and seconv are external dependencies and are not bundled.'
