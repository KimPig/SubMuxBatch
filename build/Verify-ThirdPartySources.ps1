[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

$expectedFiles = [ordered]@{
    'third-party-sources\ffmpeg\8.1\btbn-ffmpeg-builds-9acad4a9ef1583096af7836cc1e9c8cbcb4d3950.tar.gz' = 'F9BD160A794173DB7E838AD5E8841EBBA845CE6D012E830951946AA85B2967AF'
    'third-party-sources\ffmpeg\8.1\ffmpeg-330caae0c1.tar.gz' = '67B5876EE973A26F267280B2C1EB851A0AC7A502382D20E41B7A819111886AF9'
    'third-party-sources\ffmpeg\8.1\x265-020d7054dbecfd3be8e62efe33ff8a305d41856e.tar.gz' = 'ADE2FD082D1A3D7ADF89BB38EB54A5CBF6AAD6C1C7ED130C89C7BF2CD626484D'
    'third-party-sources\ffmpeg\8.1\opus-1.5.2.tar.gz' = '65C1D2F78B9F2FB20082C38CBE47C951AD5839345876E46941612EE87F9A7CE1'
    'third-party-sources\mkvtoolnix\102.0\mkvtoolnix-102.0.tar.xz' = '9F0A810F17C7DF8ADB9064A3A41D5784399BE412D19704CF080745AD7D45DA30'
    'third-party-sources\lapse\2.2.4\lapse-2.2.4.tar.gz' = 'D1AB0BDAAC81F1C038AC64FDF2578FA3DEACA1CE31980F0C9D8C0024800C8BE1'
    'third-party-sources\lapse\2.2.4\dependencies\ffmpeg-7.1.tar.xz' = '40973D44970DBC83EF302B0609F2E74982BE2D85916DD2EE7472D30678A7ABE6'
    'third-party-sources\lapse\2.2.4\dependencies\fftw-3.3.10.tar.gz' = '56C932549852CDDCFAFDAB3820B0200C7742675BE92179E59E6215B340E26467'
    'third-party-sources\lapse\2.2.4\dependencies\libfvad-532ab666.tar.gz' = 'BF639E4A5BC37B1C90E047C42524E80A1D0CECADEB6E0A61A33580FCA22A00AB'
    'third-party-sources\lapse\2.2.4\dependencies\zlib-1.3.2.tar.gz' = 'BB329A0A2CD0274D05519D61C667C062E06990D72E125EE2DFA8DE64F0119D16'
}

$requiredNotices = @(
    'licenses\ffmpeg\8.1\FFmpeg-GPL-3.0.txt',
    'licenses\ffmpeg\8.1\x265-GPL-2.0-or-later.txt',
    'licenses\ffmpeg\8.1\Opus-BSD-3-Clause.txt',
    'licenses\mkvtoolnix\102.0\COPYING.txt',
    'licenses\mkvtoolnix\102.0\README.txt',
    'licenses\mkvtoolnix\102.0\licenses\pugixml-MIT.txt',
    'licenses\mkvtoolnix\102.0\licenses\nlohmann-json-MIT.txt',
    'licenses\mkvtoolnix\102.0\licenses\QtWaitingSpinner-MIT.txt',
    'licenses\mkvtoolnix\102.0\licenses\LGPL-3.0.txt',
    'licenses\mkvtoolnix\102.0\licenses\LGPL-2.1.txt',
    'licenses\mkvtoolnix\102.0\licenses\CC-BY-3.0.txt',
    'licenses\mkvtoolnix\102.0\licenses\Boost-1.0.txt',
    'licenses\lapse\2.2.4\LAPSE-GPL-3.0-or-later.txt',
    'licenses\lapse\2.2.4\FFmpeg-LGPL-2.1-or-later.txt',
    'licenses\lapse\2.2.4\FFTW-GPL-2.0-or-later.txt',
    'licenses\lapse\2.2.4\libfvad-BSD-3-Clause.txt',
    'licenses\lapse\2.2.4\zlib-License.txt',
    'licenses\lapse\2.2.4\ONNX-Runtime-MIT.txt',
    'licenses\lapse\2.2.4\ONNX-Runtime-ThirdPartyNotices.txt',
    'licenses\lapse\2.2.4\Silero-VAD-MIT.txt'
)

foreach ($entry in $expectedFiles.GetEnumerator()) {
    $path = Join-Path $projectRoot $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing third-party source archive: $($entry.Key)"
    }

    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $entry.Value) {
        throw "Third-party source hash mismatch for $($entry.Key). Expected $($entry.Value), received $actualHash."
    }
}

foreach ($relativePath in $requiredNotices) {
    $path = Join-Path $projectRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing or empty third-party notice: $relativePath"
    }
}

Write-Host "Verified $($expectedFiles.Count) corresponding source archives and $($requiredNotices.Count) license/notice files."
