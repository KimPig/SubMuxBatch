[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [switch] $SkipTests,
    [switch] $CreateArchive,
    [string] $BundledFfmpegPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src\SubMuxBatch.App\SubMuxBatch.App.csproj'
$testPath = Join-Path $projectRoot 'tests\SubMuxBatch.Core.Tests\SubMuxBatch.Core.Tests.csproj'
$outputPath = Join-Path $projectRoot "artifacts\publish\$Runtime"
$version = Get-Date -Format 'yyyy.MM.dd'
$assemblyVersion = Get-Date -Format 'yyyy.M.d.0'
$ffmpegBinaryArchive = Join-Path $projectRoot 'third-party-binaries\ffmpeg\8.1\submux-ffmpeg-8.1-win-x64.zip'
$ffmpegBinaryArchiveHash = '222CE5F119932B1004E62D696DD7CB494895A1DF7EED015B4FA02F7FE07C0522'
$ffmpegExecutableHash = 'E65635765731D860FE2D250D51F6846AE4C751E0DC6E0691DA362554851B147E'
$ffmpegCache = Join-Path $projectRoot "artifacts\dependencies\ffmpeg\$Runtime"
$ffmpegExecutable = Join-Path $ffmpegCache 'ffmpeg.exe'
$ffmpegDigestPath = Join-Path $ffmpegCache 'executable.sha256'
$mkvToolNixVersion = '102.0'
$mkvToolNixArchiveUrl = "https://mkvtoolnix.download/windows/releases/$mkvToolNixVersion/mkvtoolnix-64-bit-$mkvToolNixVersion.zip"
$mkvToolNixArchiveHash = 'C02E918900F6D945D9307B426237E456378B79A200589AC6928DE39064409A44'
$mkvMergeHash = 'B56FFD0ED62224CF24C640F418E21538026ED444E143DDD5175E3AA7D7539196'
$mkvExtractHash = 'BD79D648762787226E8AEE23F003C0825B4FF17BC515ADAA9D61FACCEDA52346'
$mkvLocaleKoHash = '0F7568F1834CDD4F56F5EBBF293B3A5EFDBF995C89B4BBECDCF37DC66165A6C0'
$mkvToolNixCache = Join-Path $projectRoot "artifacts\dependencies\mkvtoolnix\$mkvToolNixVersion"
$mkvMergeExecutable = Join-Path $mkvToolNixCache 'mkvmerge.exe'
$mkvExtractExecutable = Join-Path $mkvToolNixCache 'mkvextract.exe'
$mkvLocaleKo = Join-Path $mkvToolNixCache 'locale\ko\LC_MESSAGES\mkvtoolnix.mo'
$lapseVersion = '2.2.4'
$lapseArchiveUrl = 'https://github.com/Schwponaco-org/lapse/releases/download/v2.2.4/lapse-windows-x64.zip'
$lapseArchiveHash = '63D7E924E4DA50A1A067B756EBB7C1517996632F7742F45D28D8CE77A742A2A6'
$lapseCache = Join-Path $projectRoot "artifacts\dependencies\lapse\$lapseVersion"
$lapseRoot = Join-Path $lapseCache 'bundle'
$lapseExecutable = Join-Path $lapseRoot 'lapse.exe'
$lapseOnnxRuntime = Join-Path $lapseRoot 'onnxruntime.dll'
$lapseModel = Join-Path $lapseRoot 'silero_vad.onnx'

& (Join-Path $PSScriptRoot 'Verify-ThirdPartySources.ps1')

function Test-FileHash([string] $Path, [string] $ExpectedHash) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() -eq $ExpectedHash
}

function Remove-VerifiedCacheDirectory([string] $Path, [string] $CacheRoot) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedRoot = ([System.IO.Path]::GetFullPath($CacheRoot)).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected dependency extraction path: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Get-BundledFfmpeg {
    [System.IO.Directory]::CreateDirectory($ffmpegCache) | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($BundledFfmpegPath)) {
        $customFfmpeg = [System.IO.Path]::GetFullPath($BundledFfmpegPath)
        if (-not (Test-Path -LiteralPath $customFfmpeg -PathType Leaf)) {
            throw "Custom FFmpeg executable not found: $customFfmpeg"
        }
        if (-not [string]::Equals($customFfmpeg, $ffmpegExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $customFfmpeg -Destination $ffmpegExecutable -Force
        }
        Set-Content -LiteralPath $ffmpegDigestPath `
            -Value (Get-FileHash -LiteralPath $ffmpegExecutable -Algorithm SHA256).Hash.ToUpperInvariant() `
            -Encoding ascii
        & (Join-Path $projectRoot 'build\ffmpeg\verify-build.ps1') -FfmpegPath $ffmpegExecutable
        return
    }
    if ($Runtime -ne 'win-x64') {
        throw 'The repository-pinned SubMux FFmpeg build supports win-x64 only. Supply -BundledFfmpegPath for another runtime.'
    }
    if (-not (Test-FileHash $ffmpegBinaryArchive $ffmpegBinaryArchiveHash)) {
        throw 'The repository-pinned SubMux FFmpeg archive is missing or failed its SHA-256 check.'
    }

    $cachedHash = if (Test-Path -LiteralPath $ffmpegDigestPath) {
        (Get-Content -LiteralPath $ffmpegDigestPath -Raw).Trim().ToUpperInvariant()
    } else {
        ''
    }
    if (-not (Test-FileHash $ffmpegExecutable $ffmpegExecutableHash) -or $cachedHash -ne $ffmpegExecutableHash) {
        $extractPath = Join-Path $ffmpegCache 'extract'
        Remove-VerifiedCacheDirectory $extractPath $ffmpegCache
        Expand-Archive -LiteralPath $ffmpegBinaryArchive -DestinationPath $extractPath -Force
        $extractedExecutable = @(Get-ChildItem -LiteralPath $extractPath -Filter 'ffmpeg.exe' -File -Recurse)
        if ($extractedExecutable.Count -ne 1 `
            -or -not (Test-FileHash $extractedExecutable[0].FullName $ffmpegExecutableHash)) {
            throw 'The repository-pinned SubMux FFmpeg executable failed its SHA-256 check.'
        }
        Copy-Item -LiteralPath $extractedExecutable[0].FullName -Destination $ffmpegExecutable -Force
        Set-Content -LiteralPath $ffmpegDigestPath -Value $ffmpegExecutableHash -Encoding ascii
        Remove-VerifiedCacheDirectory $extractPath $ffmpegCache
    }

    & (Join-Path $projectRoot 'build\ffmpeg\verify-build.ps1') -FfmpegPath $ffmpegExecutable
}

function Get-BundledMkvToolNix {
    [System.IO.Directory]::CreateDirectory($mkvToolNixCache) | Out-Null
    if ((Test-FileHash $mkvMergeExecutable $mkvMergeHash) `
        -and (Test-FileHash $mkvExtractExecutable $mkvExtractHash) `
        -and (Test-FileHash $mkvLocaleKo $mkvLocaleKoHash)) {
        return
    }

    $archivePath = Join-Path $mkvToolNixCache "mkvtoolnix-64-bit-$mkvToolNixVersion.zip"
    $extractPath = Join-Path $mkvToolNixCache 'extract'
    if (-not (Test-Path -LiteralPath $archivePath) `
        -or (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant() -ne $mkvToolNixArchiveHash) {
        Invoke-WebRequest `
            -Headers @{ 'User-Agent' = 'SubMuxBatch-Publish' } `
            -Uri $mkvToolNixArchiveUrl `
            -OutFile $archivePath
    }

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $mkvToolNixArchiveHash) {
        throw "MKVToolNix archive hash mismatch. Expected $mkvToolNixArchiveHash but received $actualHash."
    }

    Remove-VerifiedCacheDirectory $extractPath $mkvToolNixCache
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
    $extractedRoot = Join-Path $extractPath 'mkvtoolnix'
    $extractedMkvMerge = Join-Path $extractedRoot 'mkvmerge.exe'
    $extractedMkvExtract = Join-Path $extractedRoot 'mkvextract.exe'
    $extractedLocaleKo = Join-Path $extractedRoot 'locale\ko\LC_MESSAGES\mkvtoolnix.mo'
    if (-not (Test-FileHash $extractedMkvMerge $mkvMergeHash) `
        -or -not (Test-FileHash $extractedMkvExtract $mkvExtractHash) `
        -or -not (Test-FileHash $extractedLocaleKo $mkvLocaleKoHash)) {
        throw 'The extracted MKVToolNix files did not pass the pinned SHA-256 checks.'
    }

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $mkvLocaleKo)) | Out-Null
    Copy-Item -LiteralPath $extractedMkvMerge -Destination $mkvMergeExecutable -Force
    Copy-Item -LiteralPath $extractedMkvExtract -Destination $mkvExtractExecutable -Force
    Copy-Item -LiteralPath $extractedLocaleKo -Destination $mkvLocaleKo -Force
    Remove-VerifiedCacheDirectory $extractPath $mkvToolNixCache

    $mkvMergeVersion = & $mkvMergeExecutable --version 2>&1
    $mkvMergeExitCode = $LASTEXITCODE
    $mkvExtractVersion = & $mkvExtractExecutable --version 2>&1
    $mkvExtractExitCode = $LASTEXITCODE
    if ($mkvMergeExitCode -ne 0 `
        -or $mkvExtractExitCode -ne 0 `
        -or ($mkvMergeVersion -join "`n") -notmatch 'mkvmerge v102\.0' `
        -or ($mkvExtractVersion -join "`n") -notmatch 'mkvextract v102\.0') {
        throw 'The extracted MKVToolNix executables did not pass the version smoke test.'
    }
}

function Get-BundledLapse {
    [System.IO.Directory]::CreateDirectory($lapseCache) | Out-Null
    $archivePath = Join-Path $lapseCache 'lapse-windows-x64.zip'
    if (-not (Test-FileHash $archivePath $lapseArchiveHash)) {
        Invoke-WebRequest `
            -Headers @{ 'User-Agent' = 'SubMuxBatch-Publish' } `
            -Uri $lapseArchiveUrl `
            -OutFile $archivePath
    }
    if (-not (Test-FileHash $archivePath $lapseArchiveHash)) {
        throw 'LAPSE archive hash mismatch.'
    }

    Remove-VerifiedCacheDirectory $lapseRoot $lapseCache
    $extractPath = Join-Path $lapseCache 'extract'
    Remove-VerifiedCacheDirectory $extractPath $lapseCache
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
    $sourceRoot = Join-Path $extractPath 'lapse-windows-x64'
    $required = @('lapse.exe', 'onnxruntime.dll', 'silero_vad.onnx', 'LICENSE')
    foreach ($name in $required) {
        $source = Join-Path $sourceRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "The LAPSE archive is missing $name."
        }
    }
    [System.IO.Directory]::CreateDirectory($lapseRoot) | Out-Null
    foreach ($name in $required) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $lapseRoot $name) -Force
    }
    Remove-VerifiedCacheDirectory $extractPath $lapseCache

    $versionOutput = & $lapseExecutable --version 2>&1
    if ($LASTEXITCODE -ne 0 -or ($versionOutput -join "`n") -notmatch '2\.2\.4') {
        throw 'The bundled LAPSE executable did not pass the version smoke test.'
    }
}

Get-BundledFfmpeg
Get-BundledMkvToolNix
Get-BundledLapse

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
    -p:BundledMkvMergePath=$mkvMergeExecutable `
    -p:BundledMkvExtractPath=$mkvExtractExecutable `
    -p:BundledMkvLocaleKoPath=$mkvLocaleKo `
    -p:BundledLapseExePath=$lapseExecutable `
    -p:BundledLapseOnnxRuntimePath=$lapseOnnxRuntime `
    -p:BundledLapseModelPath=$lapseModel `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw 'Publish failed.'
}

# Some native NuGet packages ship their own PDB files even when DebugSymbols is
# disabled. They are not needed at runtime and would otherwise add tens of MB to
# the release archive.
Get-ChildItem -LiteralPath $outputPath -Filter '*.pdb' -File |
    Remove-Item -Force

if ($CreateArchive) {
    $releasePath = Join-Path $projectRoot "artifacts\release\v$version"
    $releaseArchivePath = Join-Path $releasePath "SubMuxBatch-v$version-$Runtime.zip"
    [System.IO.Directory]::CreateDirectory($releasePath) | Out-Null
    if (Test-Path -LiteralPath $releaseArchivePath) {
        Remove-Item -LiteralPath $releaseArchivePath -Force
    }
    Compress-Archive -Path (Join-Path $outputPath 'SubMuxBatch.exe') -DestinationPath $releaseArchivePath -CompressionLevel Optimal
    Write-Host "Release archive: $releaseArchivePath"
}
Write-Host "Published: $outputPath"
Write-Host 'MediaInfoLib, libse, FFmpeg, MKVToolNix 102.0, LAPSE 2.2.4, and SubMux Sans are bundled.'
