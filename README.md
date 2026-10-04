# SubMux Batch

SubMux Batch is a Windows desktop application that finds supported video and subtitle files with matching names, then remuxes them into a new MKV with ASS as the default subtitle track and SRT as the secondary track.

You can add individual files, select folders, or drag files and folders from File Explorer into the window. Source videos are never modified or deleted in normal mode. External subtitle files are also left untouched unless optional LAPSE synchronization produces a validated `solid` result; in that case the original subtitle is backed up before replacement. Video is always copied without re-encoding; audio is also copied unless the optional AAC conversion setting is enabled.

## Download

Prebuilt, self-contained Windows x64 packages are available on the [Releases](https://github.com/KimPig/SubMuxBatch/releases) page.

MKVToolNix 102.0, Subtitle Edit's `libse` 5.1.0, MediaInfoLib, FFmpeg 8.1, and LAPSE 2.2.4 are bundled. None requires a separate installation or path setting.

The interface supports Korean and English. With **System default**, Korean Windows uses Korean and every other system language uses English. You can override this in Settings; after saving a language change, choose whether to restart immediately or apply it the next time the application starts.

### Automatic updates

**Check for updates when the application starts** is enabled by default. You can also use **Check now** beside this option in Settings. SubMux Batch checks the latest public GitHub Release without requiring a GitHub account. When a newer date version and a matching Windows package are available, it asks before downloading anything. Choosing **Update** downloads the release ZIP, verifies its size and GitHub-provided SHA-256 digest when available, safely extracts it below `%LocalAppData%\SubMuxBatch\updates`, replaces the application files after the current process exits, and starts SubMux Batch again.

Update-check or network failures never prevent the current application from starting. The updater overwrites only files supplied by the SubMux Batch release package; unrelated files such as a separately installed `mkvmerge.exe` are left untouched. If the application directory requires administrator permission, Windows displays the standard elevation prompt when applying the update.

## Supported video inputs

Supported input containers are **MKV, MP4, M4V, MOV, AVI, TS, MTS, M2TS, and WebM**. Output is always MKV. Container conversion is performed by `mkvmerge` without re-encoding video. Audio is copied unless optional AAC conversion is enabled.

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
- **Add a SubMux processing tag to the output MKV** is enabled by default. It writes `SUBMUX_BATCH_VERSION` and `SUBMUX_BATCH_PROCESSED: Processed by SubMux Batch` as global tags. The primary ASS stores `; SUBMUX_SUBTITLE_SOURCE=ASS|SRT|SMI|ASS+SRT|ASS+SMI` in `[Script Info]`, distinguishing a generated secondary subtitle from independently supplied ASS/SRT or ASS/SMI inputs. Files produced by older versions that used `COMMENT` are still recognized.
- Source backup is split into four independent settings. **Back up source video metadata** stores `metadata.json` with the complete `mkvmerge` identification result, every non-empty MediaInfo field, source identity data, and the original Matroska tags and chapters XML when applicable. **Back up embedded subtitles** stores all embedded subtitle tracks in `subtitles.mks`. **Back up attachments** extracts fonts and other attachments as files. **Back up excluded audio tracks** stores source audio omitted by language filtering or AAC replacement in `excluded-audio.mka`.
- Backups are stored under `.submux-backup/<original source file name including extension>/`. The folder is left visible in Windows Explorer and is not assigned the Hidden attribute. MP4, AVI, TS, and other supported inputs keep their original extension in this folder name. Subtitle and audio sidecars are created by remuxing without re-encoding, and existing backup files are never overwritten. `mkvextract` is detected beside the configured `mkvmerge` executable and does not require a separate path setting. A backup error is logged as a warning and does not prevent a successfully muxed output from completing; the source file must not be deleted when such a warning occurs.
- **Clean existing metadata from the output MKV** removes the source title, global and track tags, and video track names. When only one audio track remains, its free-form track name is removed as redundant; names are preserved when multiple audio tracks remain so roles such as commentary or dubbed audio stay distinguishable. Playback-critical codec data, languages, channel layouts, and forced flags remain, while technical track statistics are regenerated for the output. Default-track selection continues to follow the existing remux and audio-filter behavior. Chapters and attachments continue to follow their separate settings. When SubMux tagging is enabled, cleanup happens first and the dedicated SubMux version and processed marker are added back; ASS-source provenance remains inside the primary ASS.
- Existing output files are never overwritten. The application creates `SubMux_Movie (1).mkv`, `(2).mkv`, and so on, including when a completed job is run again.
- If two supported videos have the same directory and filename stem, such as `Movie.mkv` and `Movie.mp4`, the item is marked invalid instead of selecting one silently.
- **Remove all existing subtitle tracks from the source video** is disabled by default. When enabled, existing tracks are replaced by the selected ASS and SRT. When disabled, every existing subtitle track is retained and the new ASS/SRT tracks are appended. The subtitle codec or representation may change when a source-container format is remuxed into Matroska; for example, MP4 Timed Text is stored as an SRT-compatible text track. Existing subtitle default flags are cleared so that only the new ASS is the default.
- When **Remove chapters from the source video** is enabled, all source chapter information is excluded from the result. Chapters are preserved by default.
- When **Remove font attachments from the source video** is enabled, font attachments are removed while cover art and other attachments are preserved.
- When **Attach ASS style font files** is enabled, SubMux Batch analyzes the font face actually referenced by visible `Dialogue` text, including inline font, weight, italic, reset, transform, and drawing-mode tags. It selects the closest installed TTF/OTF/TTC/OTC face by OpenType names and Windows font registration instead of attaching every style or family variant. If a required font cannot be found, the job is skipped without creating an output file and a warning is logged. This can be enabled together with font removal: old source fonts are removed first and the fonts required by the current ASS are then attached.
- Video, audio, attachments, and chapters are preserved by default unless their corresponding removal or filtering option is enabled. When **Keep only audio tracks in the selected language** is enabled for a multi-audio file, all English, Japanese, or Korean tracks in the selected language are retained and other audio tracks are removed. A single audio track is always preserved. If the selected language is absent, that job is skipped without creating a silent output file.
- **Convert audio to AAC** is disabled by default. It uses the bundled FFmpeg only for selected audio tracks; video and subtitles remain untouched. The channel modes preserve the original layout, convert tracks to at most stereo, or keep multichannel originals while adding an AAC stereo compatibility track. Already compatible AAC tracks are copied instead of being needlessly re-encoded. Bitrates are 96 kbps for mono, 192 kbps for stereo, 384 kbps for 3–6 channels, and 512 kbps for 7 or more channels. In the keep-and-add mode, the stereo compatibility track becomes default when it is generated from a default multichannel track.
- **LAPSE automatic synchronization** is disabled by default. Auto, global-shift-only, gradual-drift, and segment modes are available. SubMux Batch applies only a `solid` result; `unsure`, `nothing`, execution, and validation failures keep the original timing and finish with a warning. Applied results also finish with a warning when the largest actual cue adjustment reaches the configurable threshold, which is enabled at 1 second by default. When embedded-subtitle reference is enabled, the default eligible full subtitle track is preferred without coupling it to the selected audio language; SubMux-managed, forced, hearing-impaired, commentary, signs, songs, and karaoke tracks are excluded. If no suitable subtitle exists or LAPSE cannot use it, synchronization falls back to the selected/default audio track. External subtitles are replaced only after the output MKV passes final validation.
- A synchronized external subtitle is first archived under `.submux-subtitle-archive/`; matching JSON audit records are stored under its `.index` subfolder. SRT/SMI processing leaves a non-rendering LAPSE marker only in the external SRT and removes it from the SRT track muxed into the MKV. The external marker retains the settings profile and whether an embedded subtitle or audio was actually used. The styled ASS stores the LAPSE version, requested profile, actual mode/reference, verdict, offset, ratio, confidence, and source hash in `[Script Info]`. The archive JSON stores the same audit details. No LAPSE history is written to MKV global tags.
- Processing presets include subtitle, ASS, font, audio, backup, cleanup, maintenance, and LAPSE settings. Language, updates, notifications, concurrency, window state, and bundled-tool versions remain global. Presets are stored as individual JSON files under `%LOCALAPPDATA%\SubMuxBatch\presets`; the first launch creates the localized default preset (`기본.json` or `Default.json`) from the user's existing processing settings.
- The finished MKV structure is inspected before the temporary output is committed to its final filename.
- New subtitle tracks use the Korean language tag (`kor`). ASS is the default track, SRT is non-default, and neither track is forced.

## Bundled tools

Release builds include the command-line components from [MKVToolNix 102.0](https://mkvtoolnix.download/), FFmpeg 8.1, and [LAPSE 2.2.4](https://github.com/Schwponaco-org/lapse/releases/tag/v2.2.4). They are SHA-256 verified and extracted to versioned folders under `%LocalAppData%\SubMuxBatch\tools`. SubMux Batch always uses these pinned copies so that processing does not change with separately installed tool versions. **Settings → Other → Reinstall bundled tools** verifies and restores missing or modified files.

Media information shown in the queue and detail panel is read primarily with the bundled MediaInfoLib. This includes the actual container format, duration, overall and per-track bit rates, video frame rate and frame count, resolution, and audio properties. `mkvmerge` identification remains authoritative for remuxing track IDs, attachments, chapters, and output validation. The bundled library notice is included below.

## Subtitle conversion policy

Subtitle parsing and format conversion run in process with Subtitle Edit's bundled `libse` 5.1.0. When saving to a different format, SubMux Batch invokes the same native-format cleanup path used by Subtitle Edit 5.1's GUI **Save As** command. SubMux Batch adds the following policies:

- Apply the selected PlayRes and ASS `Style:` line when converting SRT to ASS
- Use CP949 as the fallback for SMI files that are neither Unicode nor valid UTF-8
- Normalize uppercase HTML tags emitted during SMI-to-SRT conversion only in the temporary SRT used to create ASS
- Flatten `<ruby>漢<rt>かん</rt></ruby>` to `漢(かん)` only in the temporary ASS-conversion input because ASS cannot represent ruby markup directly
- Restore supported inline positioning tags that the conversion pipeline may drop while converting SRT to ASS

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
Style: Default,SubMux Sans,75,&H00FFFFFF,&HFF00FFFF,&H00000000,&H02000000,0,0,0,0,100,100,0,0,1,4,0,2,0,0,80,1
```

### ASS font attachments

**Attach ASS style font files** is enabled by default for portable MKV output.

New settings use **SubMux Sans** as the default generated ASS font. Existing saved style lines are never migrated or replaced. If SubMux Sans is installed in Windows, the installed file is used; otherwise the bundled OFL-licensed copy is attached when font attachment is enabled.

SubMux Batch matches ASS `Fontname` values against OpenType full, PostScript, WWS, legacy, and typographic family names before falling back to Windows registered font names. Legacy family names are checked before broader typographic groups because common Windows ASS names such as Arial and Malgun Gothic otherwise include Narrow, Light, or Semilight designs. Family matches use the requested weight and italic state to select one face; full or PostScript names directly identify a face. Only faces referenced by actual `Dialogue` text are selected. New files are deduplicated by SHA-256, and different fonts with the same filename receive unique MKV attachment names.

When a font is found only through a Windows registered name, it is still attached and the job continues. A warning explains that players on other operating systems might not associate that Windows alias with the font's different internal name. SubMux Batch does not rewrite the ASS or attempt to predict player glyph fallback.

Font files can have separate redistribution terms. **The user is responsible for verifying that each attached font's license permits redistribution.** SubMux Batch does not make or enforce that licensing decision.

## Usage

1. Run `SubMuxBatch.exe`.
2. Add files or folders, or drag them into the application window.
3. Optionally sort by a column header or drag rows to change the processing order.
4. Right-click the queue header to show or hide the Name, Composition, Format, Duration, Codec, Action, and Status columns. The selection is saved immediately.
5. Review the detected files and processing plan, then select **Start all ready jobs**. The queue automatically scrolls to the most recently started job while preserving the current selection.

The number of concurrent jobs can be set from 1 to 8. One job at a time is recommended when the source and output are on the same hard drive; faster storage may benefit from a higher value.

### Maintenance mode

Use **Maintenance mode** for an MKV already produced by SubMux. Maintenance reconciles the existing result with the currently selected Basic-mode preset: generated ASS conversion/style, font attachment/removal, audio language/AAC policy, subtitle cleanup, chapter and metadata cleanup, SubMux tags, and LAPSE settings all come from that preset. Maintenance-category checkboxes are enabled by default and only decide which parts of the current preset are reapplied; they do not hold a second set of processing values. Backup, discovery, and ordinary output-prefix settings are not repeated because the input is already a processed MKV. MKVs not identified as SubMux outputs are skipped.

New files store `SUBMUX_SUBTITLE_SOURCE` inside the primary ASS for exact source identification. The values `ASS+SRT` and `ASS+SMI` preserve the fact that the secondary subtitle came from an independent external file instead of being generated from the ASS. For older files without the current marker, the configured legacy `Style:` fingerprints are compared exactly: the ASS must contain one `Default` style only, and its complete trimmed line must match one configured line. A match is marked `LEGACY_SRT_OR_SMI`; a non-match is marked `LEGACY_ASS_OR_UNKNOWN` and its style and fonts are preserved. Case, field spacing, and numeric spelling such as `4`, `4.0`, and `4.000` are intentionally not normalized.

ASS metadata is accepted only from `[Script Info]`. Invalid or conflicting subtitle-source markers are treated as unknown instead of falling through to a generated-subtitle rewrite. LAPSE writes a settings profile alongside its result marker; maintenance skips an identical profile, reapplies LAPSE automatically when the current mode/reference/penalty changed, and requires the force option for older markers that do not contain a profile. When a paired ASS and SRT are synchronized, both tracks are replaced in the same verified mux so their timing cannot silently diverge.

Maintenance normally writes a prefixed MKV beside the source, adding `(1)`, `(2)`, and so on instead of overwriting an existing file. The default prefix is `유지보수_` for Korean and `Maintained_` for English, and it can be customized. An optional **Replace the source MKV after completion** setting replaces the source only after the temporary output passes validation; failures and cancellation leave or restore the original. Video is copied, audio is encoded only when the selected AAC policy requires it, and the result is inspected again after it is committed. Job logs summarize subtitle-source detection, ASS/font changes, per-track audio decisions, refreshed tags, and the verified output track counts.

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
$env:FFMPEG_PATH = 'C:\Tools\ffmpeg.exe'
dotnet test tests\SubMuxBatch.Core.Tests\SubMuxBatch.Core.Tests.csproj -c Release
```

Create a self-contained Windows build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\Publish.ps1
```

The default output is written to `artifacts\publish\win-x64`. The publish script first verifies the checked-in corresponding source archives and license notices, then downloads the matching FFmpeg 8.1 LGPL build, pinned official MKVToolNix 102.0 portable package, and official LAPSE 2.2.4 Windows package, verifies their SHA-256 digests, and embeds the required tools with MediaInfoLib, libse, and SubMux Sans in the self-contained single EXE.

GitHub releases use `build\Publish-GitHubRelease.ps1`, which validates that the ZIP contains exactly one root `SubMuxBatch.exe` and always formats the description with a single `## What's changed` section.

## Project structure

- `src/SubMuxBatch.App`: WPF desktop interface
- `src/SubMuxBatch.Core`: discovery, planning, conversion, muxing, and output validation
- `tests/SubMuxBatch.Core.Tests`: unit tests and optional real-tool integration tests
- `build/Publish.ps1`: self-contained Windows publishing script
- `build/Verify-ThirdPartySources.ps1`: corresponding-source and license hash/presence checks
- `build/Publish-GitHubRelease.ps1`: validated GitHub release publishing with standardized notes
- `third-party-sources`: corresponding source archives and reproducibility details for bundled GPL tools

## Third-party notices

Open-source notices and license texts are available from **Settings → Other → Open-source licenses**. SubMux Sans is distributed under the SIL Open Font License 1.1. FFmpeg is bundled as an LGPL build and executed as a separate process for audio conversion. MKVToolNix 102.0 is GPL-2.0-only, and LAPSE 2.2.4 is GPL-3.0-or-later; both are bundled and executed as separate command-line programs. LAPSE's FFmpeg, FFTW, libfvad, zlib, ONNX Runtime, and Silero VAD notices are also included in the application. Bundled tools and dependencies remain subject to their own licenses and are not relicensed by SubMux Batch. Corresponding source archives, exact versions, hashes, and build provenance are available in [`third-party-sources`](third-party-sources).

This product uses [MediaInfo](https://mediaarea.net/MediaInfo) library, Copyright (c) 2002-2025 [MediaArea.net SARL](https://mediaarea.net/).

This product uses [libse 5.1.0](https://github.com/SubtitleEdit/subtitleedit/tree/main/src/libse), Copyright (c) 2026 Nikolaj Olsson, under the MIT License:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
