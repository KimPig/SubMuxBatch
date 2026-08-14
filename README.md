# SubMux Batch

SubMux Batch is a Windows desktop application that finds supported video and subtitle files with matching names, then remuxes them into a new MKV with ASS as the default subtitle track and SRT as the secondary track.

You can add individual files, select folders, or drag files and folders from File Explorer into the window. Source files are never modified or deleted. Video and audio streams are copied without re-encoding.

## Download

Prebuilt, self-contained Windows x64 packages are available on the [Releases](https://github.com/KimPig/SubMuxBatch/releases) page.

MKVToolNix and Subtitle Edit's command-line converter are external dependencies and are not bundled. Install or extract them separately before using the application.

## Supported video inputs

Supported input containers are **MKV, MP4, M4V, MOV, AVI, TS, MTS, M2TS, and WebM**. Output is always MKV. Container conversion is performed by `mkvmerge` without re-encoding the video or audio streams.

Whether a particular track can be remuxed depends on MKVToolNix support for the codecs inside the source file. If an unsupported stream is encountered, the job stops with an error from `mkvmerge`.

## Processing rules

| Files found | Output subtitle tracks |
| --- | --- |
| `Movie.mp4` + `Movie.ass` | Existing ASS (default) + SRT converted from the ASS (secondary) |
| `Movie.mp4` + `Movie.srt` | ASS converted from the SRT (default) + existing SRT (secondary) |
| `Movie.mp4` + `Movie.ass` + `Movie.srt` | Existing ASS (default) + existing SRT (secondary) |
| `Movie.mp4` + `Movie.smi`, without SRT | Convert SMI to SRT, then apply the rules above |
| Both SRT and SMI are present | Use the SRT and ignore the SMI |

- The default matching key is the full parent directory plus the exact filename without its extension. Matching is case-insensitive under Windows rules.
- When **Allow dot suffixes in subtitle filenames** is enabled, names such as `Movie.ko.srt`, `Movie.kor.ass`, and `Movie.release.smi` can match `Movie.mp4`. An exact filename match wins, followed by `.ko`/`.kor`, then shorter suffixes.
- All supported input containers use the same filename matching rules. The default output name is `SubMux_Movie.mkv`. You can change the prefix in Settings.
- Existing output files are never overwritten. The application creates `SubMux_Movie (1).mkv`, `(2).mkv`, and so on, including when a completed job is run again.
- If two supported videos have the same directory and filename stem, such as `Movie.mkv` and `Movie.mp4`, the item is marked invalid instead of selecting one silently.
- When **Remove all existing subtitle tracks from the source video** is enabled, existing tracks are replaced by the selected ASS and SRT. When disabled, every existing subtitle track is retained and the new ASS/SRT tracks are appended. The subtitle codec or representation may change when a source-container format is remuxed into Matroska; for example, MP4 Timed Text is stored as an SRT-compatible text track. Existing subtitle default flags are cleared so that only the new ASS is the default.
- When **Remove font attachments from the source video** is enabled, font attachments are removed while cover art and other attachments are preserved.
- Video, audio, attachments, and chapters are preserved by default. When **Keep only audio tracks in the selected language** is enabled for a multi-audio file, all English, Japanese, or Korean tracks in the selected language are retained and other audio tracks are removed. A single audio track is always preserved. If the selected language is absent, that job is skipped without creating a silent output file.
- The finished MKV structure is inspected before the temporary output is committed to its final filename.
- New subtitle tracks use the Korean language tag (`kor`). ASS is the default track, SRT is non-default, and neither track is forced.

## External dependencies

The repository and release packages do not include these applications:

- `mkvmerge.exe` from [MKVToolNix](https://mkvtoolnix.download/)
- `seconv.exe` and the libraries distributed with it from [Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit/releases) ([official command-line documentation](https://github.com/SubtitleEdit/subtitleedit/blob/main/docs/reference/command-line.md))

SubMux Batch searches for each executable in this order:

1. The path selected in Settings
2. `tools\mkvtoolnix\mkvmerge.exe` or `tools\seconv\seconv.exe` below the application directory
3. Standard installation directories
4. The Windows `PATH`

Development and integration testing used MKVToolNix 88.0 and `seconv` 5.1.0. Test a small copy of your media first when using another version. Installation and redistribution of each dependency are governed by that project's license.

## Subtitle conversion policy

Subtitle parsing and format conversion are delegated to Subtitle Edit's `seconv`. SubMux Batch adds only the following policies:

- Apply the selected PlayRes and ASS `Style:` line when converting SRT to ASS
- Pass `seconv --input-encoding-fallback:949` for CP949-encoded SMI files
- Normalize uppercase HTML tags emitted during SMI-to-SRT conversion only in the temporary SRT used to create ASS
- Flatten `<ruby>漢<rt>かん</rt></ruby>` to `漢(かん)` only in the temporary ASS-conversion input because ASS cannot represent ruby markup directly
- Restore supported inline positioning tags that `seconv` may drop while converting SRT to ASS

The SRT track added to the MKV does not pass through the ASS-compatibility preprocessing, so its original ruby markup and supported tags are retained. Subtitle Edit converts supported SRT color, position, font, size, weight, and italic markup to ASS override tags.

### SRT-to-ASS style settings

**Use the configured style when converting SRT to ASS** applies only when a new ASS file is created from SRT or SMI. When disabled, no style file is passed and Subtitle Edit's default ASS style is used. Existing ASS files are never rewritten.

Open **More** beside the option to edit:

- `PlayResX` and `PlayResY`
- Font and font size
- Primary color
- Bold and italic flags
- Outline and shadow
- Subtitle alignment
- Left, right, and vertical margins

You can also paste a complete ASS style line into **Manual Style Input**. Both `Style: Default,...` and `Default,...` are accepted. The application parses the 23 ASS v4+ fields into the form, where you can adjust individual values before saving.

Default values:

```ini
PlayResX: 1920
PlayResY: 1080
Style: Default,맑은 고딕,79.5,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,100,0.0,0,1,2.3,3.8,2,30,30,77,1
```

SubMux Batch also works around `seconv` 5.1.0 interpreting `[` and `]` in arguments as console markup, so filenames and directories such as `[Release Group] Movie.mp4` are supported.

## Usage

1. Install or extract MKVToolNix and Subtitle Edit's `seconv` package.
2. Run `SubMuxBatch.exe`.
3. If a dependency is not detected automatically, select its executable in **Settings**.
4. Add files or folders, or drag them into the application window.
5. Optionally sort by a column header or drag rows to change the processing order.
6. Right-click the queue header to show or hide the File, Composition, Format, Codec, Work, and Status columns. The selection is saved immediately.
7. Review the detected files and processing plan, then select **Start all ready jobs**. The queue automatically scrolls to the most recently started job while preserving the current selection.

The number of concurrent jobs can be set from 1 to 8. One job at a time is recommended when the source and output are on the same hard drive; faster storage may benefit from a higher value.

During a batch, the Windows taskbar icon shows aggregate progress in green. A failed batch leaves a red completion indicator, while cancelling clears the indicator.

After each non-cancelled batch, SubMux Batch shows a non-activating completion card in the lower-right corner of the monitor containing the application. The card stays visible while the pointer is over it, closes five seconds after the pointer leaves, and uses a distinct accent for success, warning, or failure. Click the card to restore the application, or use its close button to dismiss it. The completion card and the Windows system completion sound can be enabled independently in **Settings**. Notifications are enabled and sound is disabled by default; cancelled batches trigger neither.

Settings and logs are stored under `%LocalAppData%\SubMuxBatch`. On first launch after upgrading, if the new settings file does not exist but `%LocalAppData%\SubtitleBatch\settings.json` does, its settings are copied automatically. Legacy settings and logs are not deleted.

Temporary `.submuxbatch-*` workspaces are removed after a normal run. Both current and legacy `.subtitlebatch-*` workspaces left by an interrupted run are excluded from future folder scans.

## Build and test

Requirements:

- Windows
- .NET 10 SDK

```powershell
dotnet build SubMuxBatch.slnx -c Debug
dotnet test tests\SubMuxBatch.Core.Tests\SubMuxBatch.Core.Tests.csproj -c Debug
```

To include optional integration tests against real external tools, specify their paths with environment variables. Tests that cannot find a required tool are skipped safely.

```powershell
$env:MKVMERGE_PATH = 'C:\Program Files\MKVToolNix\mkvmerge.exe'
$env:SECONV_PATH = 'C:\Tools\seconv\seconv.exe'
$env:FFMPEG_PATH = 'C:\Tools\ffmpeg.exe'
dotnet test tests\SubMuxBatch.Core.Tests\SubMuxBatch.Core.Tests.csproj -c Release
```

Create a self-contained Windows build without bundling the external dependencies:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\Publish.ps1
```

The default output is written to `artifacts\publish\win-x64`. The application is published as a self-contained single EXE; MKVToolNix and `seconv` remain separate external dependencies.

## Project structure

- `src/SubMuxBatch.App`: WPF desktop interface
- `src/SubMuxBatch.Core`: discovery, planning, conversion, muxing, and output validation
- `tests/SubMuxBatch.Core.Tests`: unit tests and optional real-tool integration tests
- `build/Publish.ps1`: self-contained Windows publishing script
