[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v?\d{4}\.\d{2}\.\d{2}$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]] $Change,

    [string] $ArchivePath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$normalizedVersion = $Version.TrimStart('v')
$tag = "v$normalizedVersion"

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $ArchivePath = Join-Path $projectRoot "artifacts\release\$tag\SubMuxBatch-$tag-win-x64.zip"
}
$ArchivePath = [System.IO.Path]::GetFullPath($ArchivePath)

if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
    throw "Release archive not found: $ArchivePath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    $files = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
    if ($files.Count -ne 1 -or $files[0].FullName -ne 'SubMuxBatch.exe') {
        throw 'The release ZIP must contain exactly one root file named SubMuxBatch.exe.'
    }
} finally {
    $archive.Dispose()
}

$normalizedChanges = @($Change | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
if ($normalizedChanges.Count -eq 0) {
    throw 'At least one non-empty change is required.'
}

$body = "## What's changed`n`n" + (($normalizedChanges | ForEach-Object { "- $_" }) -join "`n") + "`n"
$notesPath = Join-Path ([System.IO.Path]::GetTempPath()) "submux-release-$([Guid]::NewGuid().ToString('N')).md"

try {
    [System.IO.File]::WriteAllText($notesPath, $body, [System.Text.UTF8Encoding]::new($false))

    & gh release view $tag --json tagName *> $null
    $releaseExists = $LASTEXITCODE -eq 0
    if ($releaseExists) {
        & gh release edit $tag --title "SubMux Batch $tag" --notes-file $notesPath
        if ($LASTEXITCODE -eq 0) {
            & gh release upload $tag $ArchivePath --clobber
        }
    } else {
        & gh release create $tag $ArchivePath --verify-tag --title "SubMux Batch $tag" --notes-file $notesPath
    }
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub release command failed for $tag."
    }
} finally {
    if (Test-Path -LiteralPath $notesPath) {
        Remove-Item -LiteralPath $notesPath -Force
    }
}

Write-Host "Published $tag with the standard What's changed format."
