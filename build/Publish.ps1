[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [switch] $SkipTests,
    [switch] $CreateArchive
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src\SubMuxBatch.App\SubMuxBatch.App.csproj'
$testPath = Join-Path $projectRoot 'tests\SubMuxBatch.Core.Tests\SubMuxBatch.Core.Tests.csproj'
$outputPath = Join-Path $projectRoot "artifacts\publish\$Runtime"
$version = Get-Date -Format 'yyyy.MM.dd'
$assemblyVersion = Get-Date -Format 'yyyy.M.d.0'
$ffmpegAssetName = switch ($Runtime) {
    'win-x64' { 'ffmpeg-n8.1-latest-win64-lgpl-8.1.zip' }
    'win-arm64' { 'ffmpeg-n8.1-latest-winarm64-lgpl-8.1.zip' }
}
$ffmpegCache = Join-Path $projectRoot "artifacts\dependencies\ffmpeg\$Runtime"
$ffmpegExecutable = Join-Path $ffmpegCache 'ffmpeg.exe'
$ffmpegDigestPath = Join-Path $ffmpegCache 'archive.sha256'

function Get-BundledFfmpeg {
    [System.IO.Directory]::CreateDirectory($ffmpegCache) | Out-Null
    $headers = @{ 'User-Agent' = 'SubMuxBatch-Publish' }
    $release = Invoke-RestMethod `
        -Headers $headers `
        -Uri 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest'
    $asset = @($release.assets | Where-Object { $_.name -eq $ffmpegAssetName })
    if ($asset.Count -ne 1) {
        throw "Could not resolve the expected FFmpeg asset: $ffmpegAssetName"
    }

    $expectedDigest = [string]$asset[0].digest
    if (-not $expectedDigest.StartsWith('sha256:', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The FFmpeg release did not provide a SHA-256 digest: $ffmpegAssetName"
    }
    $expectedHash = $expectedDigest.Substring(7).ToUpperInvariant()
    $cachedHash = if (Test-Path -LiteralPath $ffmpegDigestPath) {
        (Get-Content -LiteralPath $ffmpegDigestPath -Raw).Trim().ToUpperInvariant()
    } else {
        ''
    }
    if ((Test-Path -LiteralPath $ffmpegExecutable) -and $cachedHash -eq $expectedHash) {
        return
    }

    $archivePath = Join-Path $ffmpegCache 'ffmpeg.zip'
    $extractPath = Join-Path $ffmpegCache 'extract'
    Invoke-WebRequest `
        -Headers $headers `
        -Uri ([string]$asset[0].browser_download_url) `
        -OutFile $archivePath
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "FFmpeg archive hash mismatch. Expected $expectedHash but received $actualHash."
    }

    if (Test-Path -LiteralPath $extractPath) {
        $resolvedExtract = [System.IO.Path]::GetFullPath($extractPath)
        $resolvedCache = ([System.IO.Path]::GetFullPath($ffmpegCache)).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedExtract.StartsWith($resolvedCache, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected FFmpeg extraction path: $resolvedExtract"
        }
        Remove-Item -LiteralPath $extractPath -Recurse -Force
    }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
    $extractedExecutable = @(Get-ChildItem -LiteralPath $extractPath -Filter 'ffmpeg.exe' -File -Recurse)
    if ($extractedExecutable.Count -ne 1) {
        throw 'The downloaded FFmpeg archive did not contain exactly one ffmpeg.exe.'
    }
    Copy-Item -LiteralPath $extractedExecutable[0].FullName -Destination $ffmpegExecutable -Force
    Set-Content -LiteralPath $ffmpegDigestPath -Value $expectedHash -Encoding ascii
    Remove-Item -LiteralPath $archivePath -Force
    Remove-Item -LiteralPath $extractPath -Recurse -Force

    # FFmpeg writes its banner to stderr even on success. Windows PowerShell
    # converts those lines to NativeCommandError records when the script-wide
    # error preference is Stop, so collect them under Continue and validate the
    # native exit code explicitly below.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $versionOutput = & $ffmpegExecutable -version 2>&1
        $versionExitCode = $LASTEXITCODE
        $licenseOutput = & $ffmpegExecutable -L 2>&1
        $licenseExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $versionText = $versionOutput -join "`n"
    $licenseText = $licenseOutput -join "`n"
    $invalidFfmpeg = $versionExitCode -ne 0 `
        -or $licenseExitCode -ne 0 `
        -or $versionText -notmatch 'ffmpeg version n?8\.1' `
        -or $versionText -match '--enable-(gpl|nonfree)' `
        -or $licenseText -notmatch 'GNU Lesser General Public License'
    if ($invalidFfmpeg) {
        throw 'The downloaded FFmpeg executable did not pass the LGPL build validation.'
    }
}

Get-BundledFfmpeg

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
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$version `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:InformationalVersion=$version `
    -p:BundledFfmpegPath=$ffmpegExecutable `
    -p:BundledFfmpegResourceRid=$Runtime `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw 'Publish failed.'
}

# Some native NuGet packages ship their own PDB files even when DebugSymbols is
# disabled. They are not needed at runtime and would otherwise add tens of MB to
# the release archive.
Get-ChildItem -LiteralPath $outputPath -Filter '*.pdb' -File |
    Remove-Item -Force

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $outputPath -Force
if ($CreateArchive) {
    $releasePath = Join-Path $projectRoot "artifacts\release\v$version"
    $releaseArchivePath = Join-Path $releasePath "SubMuxBatch-v$version-$Runtime.zip"
    [System.IO.Directory]::CreateDirectory($releasePath) | Out-Null
    if (Test-Path -LiteralPath $releaseArchivePath) {
        Remove-Item -LiteralPath $releaseArchivePath -Force
    }
    Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $releaseArchivePath -CompressionLevel Optimal
    Write-Host "Release archive: $releaseArchivePath"
}
Write-Host "Published: $outputPath"
Write-Host 'MediaInfoLib, libse, FFmpeg, and SubMux Sans are bundled. MKVToolNix remains an external dependency.'
