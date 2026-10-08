[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $FfmpegPath
)

$ErrorActionPreference = 'Stop'
$FfmpegPath = [System.IO.Path]::GetFullPath($FfmpegPath)
if (-not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) {
    throw "FFmpeg executable not found: $FfmpegPath"
}

function Invoke-FfmpegText([string[]] $Arguments) {
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $FfmpegPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($exitCode -ne 0) {
        throw "FFmpeg command failed ($exitCode): $($Arguments -join ' ')`n$($output -join "`n")"
    }
    return $output -join "`n"
}

$version = Invoke-FfmpegText @('-version')
$license = Invoke-FfmpegText @('-L')
$encoders = Invoke-FfmpegText @('-hide_banner', '-encoders')
$decoders = Invoke-FfmpegText @('-hide_banner', '-decoders')
$formats = Invoke-FfmpegText @('-hide_banner', '-formats')
$filters = Invoke-FfmpegText @('-hide_banner', '-filters')

$requiredPatterns = [ordered]@{
    'GPL configuration' = '--enable-gpl'
    'x265 configuration' = '--enable-libx265'
    'Opus configuration' = '--enable-libopus'
    'GPL license text' = 'GNU General Public License'
    'libx265 encoder' = '\blibx265\b'
    'libopus encoder' = '\blibopus\b'
    'AAC encoder' = '\baac\b'
    'H.264 decoder' = '\bh264\b'
    'HEVC decoder' = '\bhevc\b'
    'AV1 decoder' = '\bav1\b'
    'VP9 decoder' = '\bvp9\b'
    'Matroska format' = '\bmatroska\b'
    'format filter' = '\bformat\b'
    'audio resample filter' = '\baresample\b'
}
$searchText = "$version`n$license`n$encoders`n$decoders`n$formats`n$filters"
foreach ($entry in $requiredPatterns.GetEnumerator()) {
    if ($searchText -notmatch $entry.Value) {
        throw "Missing required FFmpeg capability: $($entry.Key)"
    }
}
if ($version -match '--enable-nonfree') {
    throw 'The FFmpeg build unexpectedly enables nonfree components.'
}

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('.submux-ffmpeg-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($smokeRoot) | Out-Null
try {
    $video = Join-Path $smokeRoot 'main10.mkv'
    $audio = Join-Path $smokeRoot 'audio.mka'
    Invoke-FfmpegText @(
        '-hide_banner', '-loglevel', 'warning', '-y',
        '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24000/1001:duration=1',
        '-vf', 'format=yuv420p10le', '-c:v', 'libx265', '-preset', 'ultrafast', '-crf', '28',
        '-an', $video) | Out-Null
    $videoProbe = Invoke-FfmpegText @('-hide_banner', '-i', $video, '-map', '0:v:0', '-f', 'null', 'NUL')
    if ($videoProbe -notmatch 'yuv420p10le') {
        throw 'The smoke-test video is not HEVC Main10/yuv420p10le.'
    }

    Invoke-FfmpegText @(
        '-hide_banner', '-loglevel', 'warning', '-y',
        '-f', 'lavfi', '-i', 'sine=frequency=1000:duration=1',
        '-c:a', 'libopus', '-b:a', '128k', $audio) | Out-Null
    Invoke-FfmpegText @('-hide_banner', '-i', $audio, '-map', '0:a:0', '-f', 'null', 'NUL') | Out-Null
} finally {
    if (Test-Path -LiteralPath $smokeRoot) {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force
    }
}

Write-Host 'SubMux FFmpeg build validation completed successfully.'

