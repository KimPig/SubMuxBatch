using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.Fonts;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.External;

public sealed record MkvTrackInfo(
    string Type,
    string CodecId,
    bool DefaultTrack,
    bool ForcedTrack,
    string? Language,
    string? LanguageIetf,
    string? TrackName,
    int? Id = null,
    string? CodecName = null,
    string? PixelDimensions = null,
    long? DefaultDurationNanoseconds = null,
    int? AudioChannels = null,
    double? AudioSamplingFrequency = null,
    long? Bitrate = null,
    bool HearingImpaired = false,
    bool VisualImpaired = false,
    bool TextDescriptions = false,
    bool OriginalLanguage = false,
    bool Commentary = false);

public sealed record MkvAttachmentInfo(
    string? FileName,
    string? ContentType,
    string? Description,
    long? Size,
    string? Uid,
    int? Id = null);

public sealed record MkvInspection(
    IReadOnlyList<MkvTrackInfo> Tracks,
    IReadOnlyList<MkvAttachmentInfo> Attachments,
    int? ChapterCount,
    string? ContainerType = null,
    long? DurationNanoseconds = null,
    long? FileSizeBytes = null)
{
    public int AttachmentCount => Attachments.Count;
}

public sealed record MkvIdentification(MkvInspection Inspection, string Json);

public sealed record MuxResult(
    IReadOnlyList<string> Warnings,
    string StandardOutput,
    string StandardError)
{
    public bool HadWarnings => Warnings.Count > 0;
}

public sealed record SubtitleTrackReplacement(
    string Path,
    MkvTrackInfo SourceTrack);

public sealed class MkvMergeClient(string executablePath, IProcessRunner processRunner)
{
    private static readonly Regex ProgressPattern = new(@"#GUI#progress\s+(\d+)%", RegexOptions.Compiled);
    private const string WarningPrefix = "#GUI#warning";
    private static readonly HashSet<string> FontMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/font-sfnt",
        "application/font-woff",
        "application/vnd.ms-fontobject",
        "application/vnd.ms-opentype",
        "application/x-font-bdf",
        "application/x-font-opentype",
        "application/x-font-otf",
        "application/x-font-pcf",
        "application/x-font-ttf",
        "application/x-font-truetype",
        "application/x-font-type1",
        "application/x-font-woff",
        "application/x-truetype-font"
    };
    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bdf", ".cff", ".dfont", ".eot", ".fnt", ".fon", ".otc", ".otf",
        ".pcf", ".pfa", ".pfb", ".ttc", ".ttf", ".woff", ".woff2"
    };

    public async Task<MkvInspection> InspectAsync(
        string path,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default) =>
        (await IdentifyAsync(path, onOutput, cancellationToken).ConfigureAwait(false)).Inspection;

    public async Task<MkvIdentification> IdentifyAsync(
        string path,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var result = await processRunner.RunAsync(
            new ProcessRequest(
                executablePath,
                ["-J", path, "--ui-language", GetUiLanguageCode()],
                Path.GetDirectoryName(path)),
            onOutput,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode >= 2)
        {
            throw new InvalidOperationException(
                CoreText.Get("Mkv_InspectionFailed", result.ExitCode, result.StandardError.Trim()));
        }

        var inspection = ParseInspection(result.StandardOutput);
        long? fileSizeBytes = null;
        try
        {
            fileSizeBytes = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // The track metadata is still useful even if the size cannot be read.
        }
        catch (UnauthorizedAccessException)
        {
            // The track metadata is still useful even if the size cannot be read.
        }

        return new MkvIdentification(
            inspection with { FileSizeBytes = fileSizeBytes },
            result.StandardOutput);
    }

    public async Task<MuxResult> MuxAsync(
        string sourceVideo,
        string assPath,
        string srtPath,
        string outputPath,
        Action<int>? onProgress = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default,
        bool removeExistingSubtitles = true,
        bool removeExistingFontAttachments = false,
        bool removeChapters = false,
        AudioTrackLanguage? keepOnlyAudioLanguage = null,
        IReadOnlyList<FontAttachmentFile>? fontAttachments = null,
        string? globalTagsPath = null,
        bool cleanOutputMetadata = false,
        AudioMuxPlan? audioMuxPlan = null)
    {
        var arguments = new List<string>
        {
            "--gui-mode",
            "--ui-language",
            GetUiLanguageCode(),
            "-o",
            outputPath
        };

        if (cleanOutputMetadata)
        {
            arguments.Add("--title");
            arguments.Add(string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(globalTagsPath))
        {
            if (!File.Exists(globalTagsPath))
            {
                throw new FileNotFoundException(CoreText.Get("Mkv_GlobalTagsMissing"), globalTagsPath);
            }

            arguments.Add("--global-tags");
            arguments.Add(globalTagsPath);
        }

        MkvInspection? sourceInspection = null;
        if (!removeExistingSubtitles
            || removeExistingFontAttachments
            || keepOnlyAudioLanguage.HasValue
            || cleanOutputMetadata
            || audioMuxPlan is not null)
        {
            sourceInspection = await InspectAsync(sourceVideo, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (removeExistingSubtitles)
        {
            arguments.Add("--no-subtitles");
        }
        else
        {
            // Input options must precede the input file they apply to. Explicitly clear
            // every existing subtitle default flag so the newly appended ASS is the
            // only subtitle advertised as default in the result.
            foreach (var track in sourceInspection!.Tracks.Where(static track => track.Type == "subtitles"))
            {
                if (track.Id is null)
                {
                    throw new InvalidOperationException(CoreText.Get("Mkv_SubtitleTrackIdMissing"));
                }

                arguments.Add("--default-track-flag");
                arguments.Add($"{track.Id}:no");
            }
        }

        if (removeChapters)
        {
            arguments.Add("--no-chapters");
        }

        if (cleanOutputMetadata)
        {
            arguments.Add("--no-global-tags");
            arguments.Add("--no-track-tags");
        }

        if (removeExistingFontAttachments && sourceInspection is not null)
        {
            var hasFontAttachments = sourceInspection.Attachments.Any(IsFontAttachment);
            if (hasFontAttachments)
            {
                var retainedAttachmentIds = sourceInspection.Attachments
                    .Where(static attachment => !IsFontAttachment(attachment))
                    .Select(static attachment => attachment.Id
                        ?? throw new InvalidOperationException(
                            CoreText.Get("Mkv_NonFontAttachmentIdMissing")))
                    .ToArray();

                if (retainedAttachmentIds.Length == 0)
                {
                    arguments.Add("--no-attachments");
                }
                else
                {
                    // This input-file option keeps only the explicitly selected
                    // non-font attachments. Cover art and other attachments survive.
                    arguments.Add("--attachments");
                    arguments.Add(string.Join(",", retainedAttachmentIds));
                }
            }
        }

        MkvTrackInfo[] retainedAudioTracks = sourceInspection is null
            ? []
            : audioMuxPlan is null
                ? GetRetainedAudioTracks(sourceInspection, keepOnlyAudioLanguage)
                : sourceInspection.Tracks
                    .Where(track => IsTrackType(track, "audio")
                                    && track.Id.HasValue
                                    && audioMuxPlan.RetainedSourceTrackIds.Contains(track.Id.Value))
                    .ToArray();
        if (audioMuxPlan is not null && sourceInspection is not null)
        {
            if (audioMuxPlan.RetainedSourceTrackIds.Count == 0)
            {
                arguments.Add("--no-audio");
            }
            else
            {
                arguments.Add("--audio-tracks");
                arguments.Add(string.Join(",", audioMuxPlan.RetainedSourceTrackIds.Order()));
            }

            foreach (var (trackId, isDefault) in audioMuxPlan.SourceDefaultTrackOverrides.OrderBy(static pair => pair.Key))
            {
                arguments.Add("--default-track-flag");
                arguments.Add($"{trackId}:{(isDefault ? "yes" : "no")}");
            }
        }
        else if (keepOnlyAudioLanguage is { } audioLanguage && sourceInspection is not null)
        {
            var sourceAudioTracks = sourceInspection.Tracks
                .Where(static track => IsTrackType(track, "audio"))
                .ToArray();
            if (sourceAudioTracks.Length > 1)
            {
                if (retainedAudioTracks.Length == 0)
                {
                    throw new JobSkippedException(
                        CoreText.Get("Mkv_AudioLanguageNotFound", GetAudioLanguageDisplayName(audioLanguage)));
                }

                var retainedAudioIds = retainedAudioTracks
                    .Select(static track => track.Id
                        ?? throw new InvalidOperationException(
                            CoreText.Get("Mkv_AudioTrackIdMissing")))
                    .ToArray();
                arguments.Add("--audio-tracks");
                arguments.Add(string.Join(",", retainedAudioIds));

                if (!retainedAudioTracks.Any(static track => track.DefaultTrack))
                {
                    arguments.Add("--default-track-flag");
                    arguments.Add($"{retainedAudioIds[0]}:yes");
                }
            }
        }

        var outputAudioTrackCount = retainedAudioTracks.Length + (audioMuxPlan?.GeneratedTracks.Count ?? 0);
        if (cleanOutputMetadata && sourceInspection is not null)
        {
            foreach (var videoTrack in sourceInspection.Tracks.Where(static track => IsTrackType(track, "video")))
            {
                AddClearedTrackName(arguments, videoTrack);
            }

            // A single retained audio track needs no free-form label: its language,
            // codec and channel layout remain as structured track metadata.
            // Preserve names when multiple audio tracks remain so labels such as
            // commentary or dubbed audio can still distinguish them.
            if (outputAudioTrackCount == 1 && retainedAudioTracks.Length == 1)
            {
                AddClearedTrackName(arguments, retainedAudioTracks[0]);
            }
        }

        arguments.Add(sourceVideo);

        foreach (var generatedAudioTrack in audioMuxPlan?.GeneratedTracks ?? [])
        {
            AddGeneratedAudio(
                arguments,
                generatedAudioTrack,
                cleanOutputMetadata && outputAudioTrackCount == 1);
        }

        AddSubtitle(
            arguments,
            assPath,
            CoreText.Get("Mkv_AssTrackName"),
            isDefault: true);
        AddSubtitle(
            arguments,
            srtPath,
            CoreText.Get("Mkv_SrtTrackName"),
            isDefault: false);
        foreach (var fontAttachment in fontAttachments ?? [])
        {
            AddFontAttachment(arguments, fontAttachment);
        }

        void HandleOutput(string line)
        {
            onOutput?.Invoke(line);
            var match = ProgressPattern.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
            {
                onProgress?.Invoke(Math.Clamp(percent, 0, 100));
            }
        }

        var result = await processRunner.RunAsync(
            new ProcessRequest(executablePath, arguments, Path.GetDirectoryName(outputPath)),
            HandleOutput,
            cancellationToken).ConfigureAwait(false);

        var warnings = ExtractWarnings(result);
        var fatalReadWarnings = warnings.Where(IsFatalSourceReadWarning).ToArray();
        if (fatalReadWarnings.Length > 0)
        {
            TryDeleteIncompleteOutput(outputPath);
            var isMatroskaCorruption = fatalReadWarnings.Any(IsFatalMatroskaWarning);
            throw new InvalidOperationException(
                CoreText.Get(
                    isMatroskaCorruption ? "Mkv_MatroskaSourceCorrupted" : "Mkv_SourceReadFailed",
                    string.Join(Environment.NewLine, warnings)));
        }

        if (result.ExitCode >= 2 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException(
                CoreText.Get("Mkv_MuxFailed", result.ExitCode, details.Trim()));
        }

        return new MuxResult(
            warnings,
            result.StandardOutput,
            result.StandardError);
    }

    public async Task<MuxResult> MaintainAsync(
        string sourceVideo,
        string outputPath,
        IReadOnlyList<SubtitleTrackReplacement>? subtitleReplacements,
        IReadOnlyList<FontAttachmentFile>? fontAttachments,
        bool removeExistingFontAttachments,
        string? globalTagsPath,
        AudioMuxPlan? audioMuxPlan,
        IReadOnlySet<int>? retainedSubtitleTrackIds,
        bool removeChapters,
        bool cleanOutputMetadata,
        Action<int>? onProgress = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(sourceVideo, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var arguments = new List<string>
        {
            "--gui-mode", "--ui-language", GetUiLanguageCode(), "-o", outputPath
        };

        if (cleanOutputMetadata)
        {
            arguments.Add("--title");
            arguments.Add(string.Empty);
            arguments.Add("--no-track-tags");
            if (string.IsNullOrWhiteSpace(globalTagsPath)) arguments.Add("--no-global-tags");
        }

        if (removeChapters) arguments.Add("--no-chapters");

        if (!string.IsNullOrWhiteSpace(globalTagsPath))
        {
            arguments.Add("--no-global-tags");
            arguments.Add("--global-tags");
            arguments.Add(globalTagsPath);
        }

        var replacementIds = (subtitleReplacements ?? [])
            .Select(static replacement => replacement.SourceTrack.Id
                ?? throw new InvalidOperationException(CoreText.Get("Mkv_SubtitleTrackIdMissing")))
            .ToHashSet();
        if (retainedSubtitleTrackIds is not null || replacementIds.Count > 0)
        {
            var retainedSubtitleIds = inspection.Tracks
                .Where(static track => IsTrackType(track, "subtitles"))
                .Where(track => track.Id is not null && !replacementIds.Contains(track.Id.Value))
                .Where(track => retainedSubtitleTrackIds is null
                                || retainedSubtitleTrackIds.Contains(track.Id!.Value))
                .Select(track => track.Id ?? throw new InvalidOperationException(CoreText.Get("Mkv_SubtitleTrackIdMissing")))
                .ToArray();
            if (retainedSubtitleIds.Length == 0)
            {
                arguments.Add("--no-subtitles");
            }
            else
            {
                arguments.Add("--subtitle-tracks");
                arguments.Add(string.Join(",", retainedSubtitleIds));
            }
        }

        if (removeExistingFontAttachments && inspection.Attachments.Any(IsFontAttachment))
        {
            var retainedAttachmentIds = inspection.Attachments
                .Where(static attachment => !IsFontAttachment(attachment))
                .Select(static attachment => attachment.Id
                    ?? throw new InvalidOperationException(CoreText.Get("Mkv_NonFontAttachmentIdMissing")))
                .ToArray();
            if (retainedAttachmentIds.Length == 0)
            {
                arguments.Add("--no-attachments");
            }
            else
            {
                arguments.Add("--attachments");
                arguments.Add(string.Join(",", retainedAttachmentIds));
            }
        }

        if (audioMuxPlan is not null)
        {
            if (audioMuxPlan.RetainedSourceTrackIds.Count == 0)
            {
                arguments.Add("--no-audio");
            }
            else
            {
                arguments.Add("--audio-tracks");
                arguments.Add(string.Join(",", audioMuxPlan.RetainedSourceTrackIds.Order()));
            }
            foreach (var (trackId, isDefault) in audioMuxPlan.SourceDefaultTrackOverrides.OrderBy(static pair => pair.Key))
            {
                arguments.Add("--default-track-flag");
                arguments.Add($"{trackId}:{(isDefault ? "yes" : "no")}");
            }
        }

        if (cleanOutputMetadata)
        {
            foreach (var videoTrack in inspection.Tracks.Where(static track => IsTrackType(track, "video")))
            {
                AddClearedTrackName(arguments, videoTrack);
            }
            var retainedAudioTracks = audioMuxPlan is null
                ? inspection.Tracks.Where(static track => IsTrackType(track, "audio")).ToArray()
                : inspection.Tracks.Where(track => IsTrackType(track, "audio")
                                                    && track.Id.HasValue
                                                    && audioMuxPlan.RetainedSourceTrackIds.Contains(track.Id.Value)).ToArray();
            var outputAudioCount = retainedAudioTracks.Length + (audioMuxPlan?.GeneratedTracks.Count ?? 0);
            if (outputAudioCount == 1 && retainedAudioTracks.Length == 1)
            {
                AddClearedTrackName(arguments, retainedAudioTracks[0]);
            }
        }

        arguments.Add(sourceVideo);
        var maintenanceAudioPlan = audioMuxPlan;
        if (maintenanceAudioPlan is not null)
        {
            foreach (var generatedAudioTrack in maintenanceAudioPlan.GeneratedTracks)
            {
                var retainedCount = maintenanceAudioPlan.RetainedSourceTrackIds.Count;
                AddGeneratedAudio(
                    arguments,
                    generatedAudioTrack,
                    clearTrackName: cleanOutputMetadata && retainedCount + maintenanceAudioPlan.GeneratedTracks.Count == 1);
            }
        }

        foreach (var replacement in subtitleReplacements ?? [])
        {
            var replacementTrack = replacement.SourceTrack;
            arguments.Add("--language");
            arguments.Add($"0:{replacementTrack.LanguageIetf ?? replacementTrack.Language ?? "und"}");
            arguments.Add("--track-name");
            arguments.Add($"0:{replacementTrack.TrackName ?? CoreText.Get("Mkv_AssTrackName")}");
            arguments.Add("--default-track-flag");
            arguments.Add($"0:{(replacementTrack.DefaultTrack ? "yes" : "no")}");
            arguments.Add("--forced-display-flag");
            arguments.Add($"0:{(replacementTrack.ForcedTrack ? "yes" : "no")}");
            var charset = SubtitleCharsetDetector.DetectForMkvMerge(replacement.Path);
            if (charset is not null)
            {
                arguments.Add("--sub-charset");
                arguments.Add($"0:{charset}");
            }
            arguments.Add(replacement.Path);
        }

        foreach (var attachment in fontAttachments ?? [])
        {
            AddFontAttachment(arguments, attachment);
        }

        void HandleOutput(string line)
        {
            onOutput?.Invoke(line);
            var match = ProgressPattern.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
            {
                onProgress?.Invoke(Math.Clamp(percent, 0, 100));
            }
        }

        var result = await processRunner.RunAsync(
            new ProcessRequest(executablePath, arguments, Path.GetDirectoryName(outputPath)),
            HandleOutput,
            cancellationToken).ConfigureAwait(false);
        var warnings = ExtractWarnings(result);
        var fatalReadWarnings = warnings.Where(IsFatalSourceReadWarning).ToArray();
        if (fatalReadWarnings.Length > 0)
        {
            TryDeleteIncompleteOutput(outputPath);
            var isMatroskaCorruption = fatalReadWarnings.Any(IsFatalMatroskaWarning);
            throw new InvalidOperationException(
                CoreText.Get(
                    isMatroskaCorruption ? "Mkv_MatroskaSourceCorrupted" : "Mkv_SourceReadFailed",
                    string.Join(Environment.NewLine, warnings)));
        }
        if (result.ExitCode >= 2 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException(CoreText.Get("Mkv_MuxFailed", result.ExitCode, details.Trim()));
        }
        return new MuxResult(warnings, result.StandardOutput, result.StandardError);
    }

    private static IReadOnlyList<string> ExtractWarnings(ProcessResult result)
    {
        var reportedWarnings = ReadLines(result.StandardOutput)
            .Concat(ReadLines(result.StandardError))
            .Select(static line => line.TrimStart())
            .Where(static line => line.StartsWith(WarningPrefix, StringComparison.Ordinal))
            .Select(static line => line[WarningPrefix.Length..].Trim())
            .Where(static line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var warnings = reportedWarnings
            .Where(static warning => !IsSubtitleOrderingNotice(warning))
            .ToList();

        if (result.ExitCode == 1 && reportedWarnings.Count == 0)
        {
            warnings.Add(CoreText.Get("Mkv_WarningWithoutDetails"));
        }

        return warnings;
    }

    private static bool IsSubtitleOrderingNotice(string warning) =>
        warning.Contains(
            "All entries from this file will be sorted by their start time.",
            StringComparison.OrdinalIgnoreCase)
        || warning.Contains(
            "파일의 모든 항목은 시작 시간으로 정렬됩니다.",
            StringComparison.Ordinal);

    private static bool IsFatalSourceReadWarning(string warning)
    {
        if (IsFatalMatroskaWarning(warning))
        {
            return true;
        }

        if (!warning.Contains("Quicktime/MP4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isEnglishReadAbort = warning.Contains("Could not read", StringComparison.OrdinalIgnoreCase)
                                 && warning.Contains("chunk number", StringComparison.OrdinalIgnoreCase)
                                 && warning.Contains("Aborting", StringComparison.OrdinalIgnoreCase);
        var isKoreanReadAbort = warning.Contains("읽어올 수 없습니다", StringComparison.Ordinal)
                                && warning.Contains("청크 번호", StringComparison.Ordinal)
                                && warning.Contains("중단합니다", StringComparison.Ordinal);
        return isEnglishReadAbort || isKoreanReadAbort;
    }

    private static bool IsFatalMatroskaWarning(string warning)
    {
        var isEnglishStructureError = warning.Contains(
                                          "Matroska file structure",
                                          StringComparison.OrdinalIgnoreCase)
                                      && warning.Contains("error", StringComparison.OrdinalIgnoreCase);
        var isKoreanStructureError = warning.Contains("Matroska 파일 구조에 오류", StringComparison.Ordinal);
        var isEnglishMissingTrackHeader = warning.Contains("track number", StringComparison.OrdinalIgnoreCase)
                                          && warning.Contains("header", StringComparison.OrdinalIgnoreCase)
                                          && warning.Contains("skipped", StringComparison.OrdinalIgnoreCase);
        var isKoreanMissingTrackHeader = warning.Contains("트랙 번호", StringComparison.Ordinal)
                                         && warning.Contains("헤더", StringComparison.Ordinal)
                                         && warning.Contains("건너", StringComparison.Ordinal);
        return isEnglishStructureError
               || isKoreanStructureError
               || isEnglishMissingTrackHeader
               || isKoreanMissingTrackHeader;
    }

    private static void TryDeleteIncompleteOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
        catch (IOException)
        {
            // The owning job workspace performs another best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // The owning job workspace performs another best-effort cleanup.
        }
    }

    private static string GetUiLanguageCode() =>
        string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "ko",
            StringComparison.OrdinalIgnoreCase)
            ? "ko"
            : "en";

    private static IEnumerable<string> ReadLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    public static IReadOnlyList<string> ValidateOutput(
        MkvInspection source,
        MkvInspection output,
        bool removeExistingSubtitles = true,
        bool removeExistingFontAttachments = false,
        bool removeChapters = false,
        AudioTrackLanguage? keepOnlyAudioLanguage = null,
        IReadOnlyList<FontAttachmentFile>? addedFontAttachments = null,
        bool cleanOutputMetadata = false,
        AudioMuxPlan? audioMuxPlan = null)
    {
        var errors = new List<string>();
        var sourceAudioTracks = source.Tracks.Where(static track => IsTrackType(track, "audio")).ToArray();
        var audioFilterApplies = audioMuxPlan is null
                                 && keepOnlyAudioLanguage.HasValue
                                 && sourceAudioTracks.Length > 1;
        var sourceMediaTracks = source.Tracks
            .Where(track => !IsTrackType(track, "subtitles")
                            && (audioMuxPlan is not null
                                ? !IsTrackType(track, "audio")
                                  || (track.Id.HasValue
                                      && audioMuxPlan.RetainedSourceTrackIds.Contains(track.Id.Value))
                                : !audioFilterApplies
                                  || !IsTrackType(track, "audio")
                                  || MatchesAudioLanguage(track, keepOnlyAudioLanguage!.Value)))
            .ToArray();
        var retainedSourceAudioTracks = sourceMediaTracks
            .Where(static track => IsTrackType(track, "audio"))
            .ToArray();
        var expectedOutputAudioTrackCount = retainedSourceAudioTracks.Length
                                            + (audioMuxPlan?.GeneratedTracks.Count ?? 0);
        var outputMediaTracks = output.Tracks
            .Where(static track => !IsTrackType(track, "subtitles"))
            .ToArray();

        if (audioFilterApplies && !sourceMediaTracks.Any(static track => IsTrackType(track, "audio")))
        {
            errors.Add(CoreText.Get("Mkv_ValidationAudioMissing", GetAudioLanguageDisplayName(keepOnlyAudioLanguage!.Value)));
        }

        var expectedMediaTrackCount = sourceMediaTracks.Length + (audioMuxPlan?.GeneratedTracks.Count ?? 0);
        if (expectedMediaTrackCount != outputMediaTracks.Length)
        {
            errors.Add(CoreText.Get("Mkv_ValidationMediaTrackCount"));
        }
        else
        {
            for (var index = 0; index < sourceMediaTracks.Length; index++)
            {
                var sourceTrack = sourceMediaTracks[index];
                var outputTrack = outputMediaTracks[index];
                if (!string.Equals(sourceTrack.Type, outputTrack.Type, StringComparison.OrdinalIgnoreCase)
                    || !CodecMetadataEquals(sourceTrack, outputTrack))
                {
                    errors.Add(CoreText.Get("Mkv_ValidationMediaTrackMismatch", index + 1));
                    continue;
                }

                if (audioFilterApplies
                    && IsTrackType(sourceTrack, "audio")
                    && !MatchesAudioLanguage(outputTrack, keepOnlyAudioLanguage!.Value))
                {
                    errors.Add(
                        CoreText.Get("Mkv_ValidationWrongAudio", index + 1, GetAudioLanguageDisplayName(keepOnlyAudioLanguage.Value)));
                }

                if (IsTrackType(sourceTrack, "audio"))
                {
                    if (!LanguageMetadataPreserved(sourceTrack, outputTrack)
                        || sourceTrack.ForcedTrack != outputTrack.ForcedTrack
                        || !AudioFlagsPreserved(sourceTrack, outputTrack)
                        || !AudioTechnicalMetadataPreserved(sourceTrack, outputTrack))
                    {
                        errors.Add(CoreText.Get("Mkv_ValidationAudioMetadata", index + 1));
                    }

                    if (audioMuxPlan is not null)
                    {
                        var expectedDefault = sourceTrack.Id.HasValue
                                              && audioMuxPlan.SourceDefaultTrackOverrides.TryGetValue(
                                                  sourceTrack.Id.Value,
                                                  out var overriddenDefault)
                            ? overriddenDefault
                            : sourceTrack.DefaultTrack;
                        if (outputTrack.DefaultTrack != expectedDefault)
                        {
                            errors.Add(CoreText.Get("Mkv_ValidationAudioDefault", index + 1));
                        }
                    }
                }

                var shouldClearTrackName = cleanOutputMetadata
                                           && (IsTrackType(sourceTrack, "video")
                                               || (IsTrackType(sourceTrack, "audio")
                                                   && expectedOutputAudioTrackCount == 1));
                if (shouldClearTrackName)
                {
                    if (!string.IsNullOrWhiteSpace(outputTrack.TrackName))
                    {
                        errors.Add(CoreText.Get("Mkv_ValidationMediaTrackNameNotRemoved", index + 1));
                    }
                }
                else if (!string.Equals(sourceTrack.TrackName, outputTrack.TrackName, StringComparison.Ordinal))
                {
                    errors.Add(CoreText.Get("Mkv_ValidationMediaTrackNameNotPreserved", index + 1));
                }
            }

            if (audioMuxPlan is not null)
            {
                for (var index = 0; index < audioMuxPlan.GeneratedTracks.Count; index++)
                {
                    var expected = audioMuxPlan.GeneratedTracks[index];
                    var outputTrack = outputMediaTracks[sourceMediaTracks.Length + index];
                    var displayIndex = sourceMediaTracks.Length + index + 1;
                    if (!IsTrackType(outputTrack, "audio")
                        || !outputTrack.CodecId.Contains("AAC", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(CoreText.Get("Mkv_ValidationGeneratedAudioCodec", displayIndex));
                        continue;
                    }

                    if (!LanguageMetadataPreserved(expected.SourceTrack, outputTrack)
                        || outputTrack.ForcedTrack != expected.ForcedTrack
                        || !AudioFlagsPreserved(expected.SourceTrack, outputTrack)
                        || outputTrack.DefaultTrack != expected.DefaultTrack
                        || (expected.OutputChannels.HasValue
                            && outputTrack.AudioChannels != expected.OutputChannels))
                    {
                        errors.Add(CoreText.Get("Mkv_ValidationGeneratedAudioMetadata", displayIndex));
                    }

                    var expectedName = cleanOutputMetadata && expectedOutputAudioTrackCount == 1
                        ? null
                        : expected.TrackName;
                    if (!string.Equals(
                            expectedName ?? string.Empty,
                            outputTrack.TrackName ?? string.Empty,
                            StringComparison.Ordinal))
                    {
                        errors.Add(CoreText.Get("Mkv_ValidationMediaTrackNameNotPreserved", displayIndex));
                    }
                }
            }
        }

        var expectedAttachments = removeExistingFontAttachments
            ? source.Attachments.Where(static attachment => !IsFontAttachment(attachment)).ToArray()
            : source.Attachments.ToArray();
        var addedAttachments = addedFontAttachments ?? [];
        if (expectedAttachments.Length + addedAttachments.Count != output.AttachmentCount)
        {
            errors.Add(CoreText.Get("Mkv_ValidationAttachmentCount"));
        }
        else
        {
            var remainingAttachments = output.Attachments.ToList();
            for (var index = 0; index < expectedAttachments.Length; index++)
            {
                var matchIndex = remainingAttachments.FindIndex(
                    attachment => AttachmentMetadataEquals(expectedAttachments[index], attachment));
                if (matchIndex < 0)
                {
                    errors.Add(CoreText.Get("Mkv_ValidationPreservedAttachment", index + 1));
                }
                else
                {
                    remainingAttachments.RemoveAt(matchIndex);
                }
            }

            for (var index = 0; index < addedAttachments.Count; index++)
            {
                var expected = addedAttachments[index];
                var matchIndex = remainingAttachments.FindIndex(
                    attachment => AddedFontAttachmentMetadataEquals(expected, attachment));
                if (matchIndex < 0)
                {
                    errors.Add(CoreText.Get("Mkv_ValidationAddedFont", expected.FileName));
                }
                else
                {
                    remainingAttachments.RemoveAt(matchIndex);
                }
            }
        }

        if (removeChapters)
        {
            if (output.ChapterCount is > 0)
            {
                errors.Add(CoreText.Get("Mkv_ValidationChaptersRemoved"));
            }
        }
        else if (source.ChapterCount.HasValue && output.ChapterCount.HasValue
                 && source.ChapterCount.Value != output.ChapterCount.Value)
        {
            errors.Add(CoreText.Get("Mkv_ValidationChapterCount"));
        }

        var sourceSubtitles = source.Tracks.Where(static track => track.Type == "subtitles").ToArray();
        var outputSubtitles = output.Tracks.Where(static track => track.Type == "subtitles").ToArray();
        var expectedSubtitleCount = removeExistingSubtitles ? 2 : sourceSubtitles.Length + 2;
        if (outputSubtitles.Length != expectedSubtitleCount)
        {
            errors.Add(CoreText.Get("Mkv_ValidationSubtitleCount", expectedSubtitleCount, outputSubtitles.Length));
            return errors;
        }

        if (!removeExistingSubtitles)
        {
            for (var index = 0; index < sourceSubtitles.Length; index++)
            {
                ValidatePreservedSubtitle(
                    sourceSubtitles[index],
                    outputSubtitles[index],
                    index,
                    errors: errors);
            }
        }

        var addedAssIndex = outputSubtitles.Length - 2;
        var addedSrtIndex = outputSubtitles.Length - 1;
        ValidateSubtitle(outputSubtitles[addedAssIndex], "S_TEXT/ASS", shouldBeDefault: true, CoreText.Get("Mkv_AddedAssLabel"), errors);
        ValidateSubtitle(outputSubtitles[addedSrtIndex], "S_TEXT/UTF8", shouldBeDefault: false, CoreText.Get("Mkv_AddedSrtLabel"), errors);
        if (cleanOutputMetadata
            && !string.Equals(
                outputSubtitles[addedAssIndex].TrackName,
                CoreText.Get("Mkv_AssTrackName"),
                StringComparison.Ordinal))
        {
            errors.Add(CoreText.Get("Mkv_ValidationAddedTrackName", CoreText.Get("Mkv_AddedAssLabel")));
        }

        if (cleanOutputMetadata
            && !string.Equals(
                outputSubtitles[addedSrtIndex].TrackName,
                CoreText.Get("Mkv_SrtTrackName"),
                StringComparison.Ordinal))
        {
            errors.Add(CoreText.Get("Mkv_ValidationAddedTrackName", CoreText.Get("Mkv_AddedSrtLabel")));
        }

        if (outputSubtitles.Count(static track => track.DefaultTrack) != 1)
        {
            errors.Add(CoreText.Get("Mkv_ValidationOnlyAssDefault"));
        }

        return errors;
    }

    private static void ValidatePreservedSubtitle(
        MkvTrackInfo source,
        MkvTrackInfo output,
        int index,
        ICollection<string> errors)
    {
        var label = CoreText.Get("Mkv_PreservedSubtitleLabel", index + 1);
        if (!CodecMetadataEquals(source, output))
        {
            errors.Add(CoreText.Get("Mkv_ValidationCodecNotPreserved", label, source.CodecId, output.CodecId));
        }

        if (source.ForcedTrack != output.ForcedTrack)
        {
            errors.Add(CoreText.Get("Mkv_ValidationForcedNotPreserved", label));
        }

        if (!LanguageMetadataPreserved(source, output))
        {
            errors.Add(CoreText.Get("Mkv_ValidationLanguageNotPreserved", label));
        }

        if (!string.Equals(source.TrackName, output.TrackName, StringComparison.Ordinal))
        {
            errors.Add(CoreText.Get("Mkv_ValidationNameNotPreserved", label));
        }

        if (output.DefaultTrack)
        {
            errors.Add(CoreText.Get("Mkv_ValidationDefaultNotCleared", label));
        }
    }

    private static MkvTrackInfo[] GetRetainedAudioTracks(
        MkvInspection source,
        AudioTrackLanguage? keepOnlyAudioLanguage)
    {
        var sourceAudioTracks = source.Tracks
            .Where(static track => IsTrackType(track, "audio"))
            .ToArray();
        if (keepOnlyAudioLanguage is not { } audioLanguage || sourceAudioTracks.Length <= 1)
        {
            return sourceAudioTracks;
        }

        return sourceAudioTracks
            .Where(track => MatchesAudioLanguage(track, audioLanguage))
            .ToArray();
    }

    private static void AddClearedTrackName(List<string> arguments, MkvTrackInfo track)
    {
        if (track.Id is null)
        {
            throw new InvalidOperationException(CoreText.Get("Mkv_TrackNameCleanupIdMissing"));
        }

        arguments.Add("--track-name");
        arguments.Add($"{track.Id}:");
    }

    private static void AddSubtitle(List<string> arguments, string path, string trackName, bool isDefault)
    {
        arguments.Add("--language");
        arguments.Add("0:kor");
        arguments.Add("--track-name");
        arguments.Add($"0:{trackName}");
        arguments.Add("--default-track-flag");
        arguments.Add($"0:{(isDefault ? "yes" : "no")}");
        arguments.Add("--forced-display-flag");
        arguments.Add("0:no");

        var charset = SubtitleCharsetDetector.DetectForMkvMerge(path);
        if (charset is not null)
        {
            arguments.Add("--sub-charset");
            arguments.Add($"0:{charset}");
        }

        arguments.Add(path);
    }

    private static void AddGeneratedAudio(
        List<string> arguments,
        GeneratedAudioTrack track,
        bool clearTrackName)
    {
        if (!File.Exists(track.FilePath))
        {
            throw new FileNotFoundException(CoreText.Get("Mkv_GeneratedAudioMissing"), track.FilePath);
        }

        var language = !IsUndeterminedLanguage(track.SourceTrack.LanguageIetf)
            ? track.SourceTrack.LanguageIetf
            : track.SourceTrack.Language;
        if (!string.IsNullOrWhiteSpace(language))
        {
            arguments.Add("--language");
            arguments.Add($"0:{language}");
        }
        arguments.Add("--track-name");
        arguments.Add($"0:{(clearTrackName ? string.Empty : track.TrackName ?? string.Empty)}");
        arguments.Add("--default-track-flag");
        arguments.Add($"0:{(track.DefaultTrack ? "yes" : "no")}");
        arguments.Add("--forced-display-flag");
        arguments.Add($"0:{(track.ForcedTrack ? "yes" : "no")}");
        AddTrackFlag(arguments, "--hearing-impaired-flag", track.SourceTrack.HearingImpaired);
        AddTrackFlag(arguments, "--visual-impaired-flag", track.SourceTrack.VisualImpaired);
        AddTrackFlag(arguments, "--text-descriptions-flag", track.SourceTrack.TextDescriptions);
        AddTrackFlag(arguments, "--original-flag", track.SourceTrack.OriginalLanguage);
        AddTrackFlag(arguments, "--commentary-flag", track.SourceTrack.Commentary);
        // The temporary MKA is an implementation detail. Do not copy FFmpeg's
        // encoder/global tags into the final MKV, regardless of metadata cleanup.
        arguments.Add("--no-global-tags");
        arguments.Add("--no-track-tags");
        arguments.Add(track.FilePath);
    }

    private static void AddTrackFlag(List<string> arguments, string option, bool value)
    {
        arguments.Add(option);
        arguments.Add($"0:{(value ? "yes" : "no")}");
    }

    private static void AddFontAttachment(List<string> arguments, FontAttachmentFile attachment)
    {
        if (!File.Exists(attachment.FilePath))
        {
            throw new FileNotFoundException(CoreText.Get("Mkv_FontAttachmentMissing"), attachment.FilePath);
        }

        arguments.Add("--attachment-mime-type");
        arguments.Add(attachment.MimeType);
        arguments.Add("--attachment-name");
        arguments.Add(attachment.FileName);
        arguments.Add("--attach-file");
        arguments.Add(attachment.FilePath);
    }

    private static void ValidateSubtitle(
        MkvTrackInfo track,
        string expectedCodec,
        bool shouldBeDefault,
        string label,
        ICollection<string> errors)
    {
        if (!string.Equals(track.CodecId, expectedCodec, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(CoreText.Get("Mkv_ValidationWrongCodec", label, expectedCodec, track.CodecId));
        }

        if (track.DefaultTrack != shouldBeDefault)
        {
            errors.Add(CoreText.Get("Mkv_ValidationWrongDefault", label));
        }

        if (track.ForcedTrack)
        {
            errors.Add(CoreText.Get("Mkv_ValidationForcedSet", label));
        }

        var isKorean = string.Equals(track.Language, "kor", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(track.Language, "ko", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(track.LanguageIetf, "ko", StringComparison.OrdinalIgnoreCase)
                       || track.LanguageIetf?.StartsWith("ko-", StringComparison.OrdinalIgnoreCase) == true;
        if (!isKorean)
        {
            errors.Add(CoreText.Get("Mkv_ValidationNotKorean", label));
        }
    }

    internal static bool CodecMetadataEquals(MkvTrackInfo source, MkvTrackInfo output)
    {
        var sourceCodec = string.IsNullOrWhiteSpace(source.CodecName)
            ? source.CodecId
            : source.CodecName;
        var outputCodec = string.IsNullOrWhiteSpace(output.CodecName)
            ? output.CodecId
            : output.CodecName;
        if (IsUtf8TextSubtitle(source, sourceCodec)
            && IsUtf8TextSubtitle(output, outputCodec))
        {
            return true;
        }

        return string.Equals(
            sourceCodec.Trim(),
            outputCodec.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUtf8TextSubtitle(MkvTrackInfo track, string codec) =>
        IsTrackType(track, "subtitles")
        && (codec.Contains("Timed Text", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("tx3g", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("SubRip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(track.CodecId, "S_TEXT/UTF8", StringComparison.OrdinalIgnoreCase));

    internal static bool LanguageMetadataPreserved(MkvTrackInfo source, MkvTrackInfo output) =>
        LanguageFieldPreserved(source.Language, output.Language)
        && LanguageFieldPreserved(source.LanguageIetf, output.LanguageIetf);

    internal static bool AudioTechnicalMetadataPreserved(MkvTrackInfo source, MkvTrackInfo output) =>
        (!source.AudioChannels.HasValue || source.AudioChannels == output.AudioChannels)
        && (!source.AudioSamplingFrequency.HasValue
            || (output.AudioSamplingFrequency.HasValue
                && Math.Abs(source.AudioSamplingFrequency.Value - output.AudioSamplingFrequency.Value) < 0.01));

    internal static bool AudioFlagsPreserved(MkvTrackInfo source, MkvTrackInfo output) =>
        source.HearingImpaired == output.HearingImpaired
        && source.VisualImpaired == output.VisualImpaired
        && source.TextDescriptions == output.TextDescriptions
        && source.OriginalLanguage == output.OriginalLanguage
        && source.Commentary == output.Commentary;

    private static bool LanguageFieldPreserved(string? source, string? output) =>
        IsUndeterminedLanguage(source)
        || string.Equals(source!.Trim(), output?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool IsTrackType(MkvTrackInfo track, string type) =>
        string.Equals(track.Type, type, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAudioLanguage(MkvTrackInfo track, AudioTrackLanguage language)
    {
        var (legacyCode, ietfCode) = language switch
        {
            AudioTrackLanguage.English => ("eng", "en"),
            AudioTrackLanguage.Japanese => ("jpn", "ja"),
            AudioTrackLanguage.Korean => ("kor", "ko"),
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
        };

        // LanguageIETF is the newer Matroska field. When it contains a useful
        // value it is authoritative; the legacy field is only a fallback.
        if (!IsUndeterminedLanguage(track.LanguageIetf))
        {
            return MatchesLanguageCode(track.LanguageIetf, legacyCode, ietfCode);
        }

        return MatchesLanguageCode(track.Language, legacyCode, ietfCode);
    }

    private static bool MatchesLanguageCode(string? value, string legacyCode, string ietfCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return string.Equals(normalized, legacyCode, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, ietfCode, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith($"{ietfCode}-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUndeterminedLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), "und", StringComparison.OrdinalIgnoreCase);

    private static string GetAudioLanguageDisplayName(AudioTrackLanguage language) => language switch
    {
        AudioTrackLanguage.English => CoreText.Get("Language_English"),
        AudioTrackLanguage.Japanese => CoreText.Get("Language_Japanese"),
        AudioTrackLanguage.Korean => CoreText.Get("Language_Korean"),
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
    };

    public static bool IsFontAttachment(MkvAttachmentInfo attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var contentType = attachment.ContentType?.Split(';', 2)[0].Trim();
        if (!string.IsNullOrWhiteSpace(contentType)
            && (contentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
                || FontMimeTypes.Contains(contentType)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(attachment.FileName))
        {
            return false;
        }

        return FontExtensions.Contains(Path.GetExtension(attachment.FileName));
    }

    internal static bool AttachmentMetadataEquals(MkvAttachmentInfo expected, MkvAttachmentInfo actual) =>
        string.Equals(expected.FileName, actual.FileName, StringComparison.Ordinal)
        && string.Equals(expected.ContentType, actual.ContentType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.Description, actual.Description, StringComparison.Ordinal)
        && expected.Size == actual.Size
        && string.Equals(expected.Uid, actual.Uid, StringComparison.Ordinal);

    internal static bool AddedFontAttachmentMetadataEquals(
        FontAttachmentFile expected,
        MkvAttachmentInfo actual) =>
        string.Equals(expected.FileName, actual.FileName, StringComparison.Ordinal)
        && string.Equals(expected.MimeType, actual.ContentType, StringComparison.OrdinalIgnoreCase)
        && expected.Size == actual.Size;

    private static MkvInspection ParseInspection(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tracks = new List<MkvTrackInfo>();

            var container = root.TryGetProperty("container", out var containerElement)
                ? containerElement
                : default;
            var containerProperties = container.ValueKind == JsonValueKind.Object
                && container.TryGetProperty("properties", out var propertiesElement)
                    ? propertiesElement
                    : default;
            var containerType = GetString(container, "type");
            var durationNanoseconds = GetInt64(containerProperties, "duration");

            if (root.TryGetProperty("tracks", out var tracksElement))
            {
                foreach (var trackElement in tracksElement.EnumerateArray())
                {
                    var type = GetString(trackElement, "type") ?? "unknown";
                    var properties = trackElement.TryGetProperty("properties", out var value) ? value : default;
                    var codecId = properties.ValueKind == JsonValueKind.Object
                        ? GetString(properties, "codec_id")
                        : null;
                    codecId ??= GetString(trackElement, "codec") ?? "unknown";

                    tracks.Add(new MkvTrackInfo(
                        type,
                        codecId,
                        GetBoolean(properties, "default_track"),
                        GetBoolean(properties, "forced_track"),
                        GetString(properties, "language"),
                        GetString(properties, "language_ietf"),
                        GetString(properties, "track_name"),
                        GetInt32(trackElement, "id"),
                        GetString(trackElement, "codec"),
                        GetString(properties, "pixel_dimensions"),
                        GetInt64(properties, "default_duration"),
                        GetInt32(properties, "audio_channels"),
                        GetDouble(properties, "audio_sampling_frequency"),
                        GetFlexibleInt64(properties, "tag_bps"),
                        GetBoolean(properties, "hearing_impaired"),
                        GetBoolean(properties, "visual_impaired"),
                        GetBoolean(properties, "text_descriptions"),
                        GetBoolean(properties, "original"),
                        GetBoolean(properties, "commentary")));
                }
            }

            var parsedAttachments = new List<MkvAttachmentInfo>();
            if (root.TryGetProperty("attachments", out var attachments)
                && attachments.ValueKind == JsonValueKind.Array)
            {
                foreach (var attachment in attachments.EnumerateArray())
                {
                    var properties = attachment.TryGetProperty("properties", out var attachmentProperties)
                        ? attachmentProperties
                        : default;
                    parsedAttachments.Add(new MkvAttachmentInfo(
                        GetString(attachment, "file_name") ?? GetString(properties, "file_name"),
                        GetString(attachment, "content_type") ?? GetString(properties, "content_type"),
                        GetString(attachment, "description") ?? GetString(properties, "description"),
                        GetInt64(attachment, "size") ?? GetInt64(properties, "size"),
                        GetScalarString(properties, "uid") ?? GetScalarString(attachment, "uid"),
                        GetInt32(attachment, "id") ?? GetInt32(properties, "id")));
                }
            }

            int? chapterCount = null;
            if (!root.TryGetProperty("chapters", out var chapterGroups))
            {
                chapterCount = 0;
            }
            else if (chapterGroups.ValueKind == JsonValueKind.Array)
            {
                chapterCount = chapterGroups.EnumerateArray()
                    .Sum(static chapter => GetInt32(chapter, "num_entries") ?? 0);
            }

            return new MkvInspection(
                tracks,
                parsedAttachments,
                chapterCount,
                containerType,
                durationNanoseconds);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(CoreText.Get("Mkv_JsonParseFailed"), exception);
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var value)
               && value.ValueKind is JsonValueKind.True or JsonValueKind.False
               && value.GetBoolean();
    }

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt32(out var result)
            ? result
            : null;

    private static long? GetInt64(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt64(out var result)
            ? result
            : null;

    private static double? GetDouble(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.TryGetDouble(out var result)
            ? result
            : null;

    private static long? GetFlexibleInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numericValue))
        {
            return numericValue;
        }

        return value.ValueKind == JsonValueKind.String
               && long.TryParse(value.GetString(), out var stringValue)
            ? stringValue
            : null;
    }

    private static string? GetScalarString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }
}
