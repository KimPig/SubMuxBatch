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
    long? Bitrate = null);

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

public sealed record MuxResult(
    IReadOnlyList<string> Warnings,
    string StandardOutput,
    string StandardError)
{
    public bool HadWarnings => Warnings.Count > 0;
}

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

        return inspection with { FileSizeBytes = fileSizeBytes };
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
        string? globalTagsPath = null)
    {
        var arguments = new List<string>
        {
            "--gui-mode",
            "--ui-language",
            GetUiLanguageCode(),
            "-o",
            outputPath
        };

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
        if (!removeExistingSubtitles || removeExistingFontAttachments || keepOnlyAudioLanguage.HasValue)
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

        if (keepOnlyAudioLanguage is { } audioLanguage && sourceInspection is not null)
        {
            var sourceAudioTracks = sourceInspection.Tracks
                .Where(static track => IsTrackType(track, "audio"))
                .ToArray();
            if (sourceAudioTracks.Length > 1)
            {
                var retainedAudioTracks = sourceAudioTracks
                    .Where(track => MatchesAudioLanguage(track, audioLanguage))
                    .ToArray();
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

        arguments.Add(sourceVideo);

        AddSubtitle(arguments, assPath, CoreText.Get("Mkv_AssTrackName"), isDefault: true);
        AddSubtitle(arguments, srtPath, CoreText.Get("Mkv_SrtTrackName"), isDefault: false);
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

        if (result.ExitCode >= 2 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException(
                CoreText.Get("Mkv_MuxFailed", result.ExitCode, details.Trim()));
        }

        return new MuxResult(
            ExtractWarnings(result),
            result.StandardOutput,
            result.StandardError);
    }

    private static IReadOnlyList<string> ExtractWarnings(ProcessResult result)
    {
        var warnings = ReadLines(result.StandardOutput)
            .Concat(ReadLines(result.StandardError))
            .Select(static line => line.TrimStart())
            .Where(static line => line.StartsWith(WarningPrefix, StringComparison.Ordinal))
            .Select(static line => line[WarningPrefix.Length..].Trim())
            .Where(static line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (result.ExitCode == 1 && warnings.Count == 0)
        {
            warnings.Add(CoreText.Get("Mkv_WarningWithoutDetails"));
        }

        return warnings;
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
        IReadOnlyList<FontAttachmentFile>? addedFontAttachments = null)
    {
        var errors = new List<string>();
        var sourceAudioTracks = source.Tracks.Where(static track => IsTrackType(track, "audio")).ToArray();
        var audioFilterApplies = keepOnlyAudioLanguage.HasValue && sourceAudioTracks.Length > 1;
        var sourceMediaTracks = source.Tracks
            .Where(track => !IsTrackType(track, "subtitles")
                            && (!audioFilterApplies
                                || !IsTrackType(track, "audio")
                                || MatchesAudioLanguage(track, keepOnlyAudioLanguage!.Value)))
            .ToArray();
        var outputMediaTracks = output.Tracks
            .Where(static track => !IsTrackType(track, "subtitles"))
            .ToArray();

        if (audioFilterApplies && !sourceMediaTracks.Any(static track => IsTrackType(track, "audio")))
        {
            errors.Add(CoreText.Get("Mkv_ValidationAudioMissing", GetAudioLanguageDisplayName(keepOnlyAudioLanguage!.Value)));
        }

        if (sourceMediaTracks.Length != outputMediaTracks.Length)
        {
            errors.Add(CoreText.Get("Mkv_ValidationMediaTrackCount"));
        }
        else
        {
            for (var index = 0; index < sourceMediaTracks.Length; index++)
            {
                if (!string.Equals(sourceMediaTracks[index].Type, outputMediaTracks[index].Type, StringComparison.OrdinalIgnoreCase)
                    || !CodecMetadataEquals(sourceMediaTracks[index], outputMediaTracks[index]))
                {
                    errors.Add(CoreText.Get("Mkv_ValidationMediaTrackMismatch", index + 1));
                }
                else if (audioFilterApplies
                         && IsTrackType(sourceMediaTracks[index], "audio")
                         && !MatchesAudioLanguage(outputMediaTracks[index], keepOnlyAudioLanguage!.Value))
                {
                    errors.Add(
                        CoreText.Get("Mkv_ValidationWrongAudio", index + 1, GetAudioLanguageDisplayName(keepOnlyAudioLanguage.Value)));
                }
                else if (audioFilterApplies
                         && IsTrackType(sourceMediaTracks[index], "audio")
                         && (!LanguageMetadataPreserved(
                                 sourceMediaTracks[index],
                                 outputMediaTracks[index])
                             || !string.Equals(
                                 sourceMediaTracks[index].TrackName,
                                 outputMediaTracks[index].TrackName,
                                 StringComparison.Ordinal)
                             || sourceMediaTracks[index].ForcedTrack != outputMediaTracks[index].ForcedTrack))
                {
                    errors.Add(
                        CoreText.Get("Mkv_ValidationAudioMetadata", index + 1));
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
                ValidatePreservedSubtitle(sourceSubtitles[index], outputSubtitles[index], index, errors);
            }
        }

        var addedAssIndex = outputSubtitles.Length - 2;
        var addedSrtIndex = outputSubtitles.Length - 1;
        ValidateSubtitle(outputSubtitles[addedAssIndex], "S_TEXT/ASS", shouldBeDefault: true, CoreText.Get("Mkv_AddedAssLabel"), errors);
        ValidateSubtitle(outputSubtitles[addedSrtIndex], "S_TEXT/UTF8", shouldBeDefault: false, CoreText.Get("Mkv_AddedSrtLabel"), errors);

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

    private static bool CodecMetadataEquals(MkvTrackInfo source, MkvTrackInfo output)
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

    private static bool LanguageMetadataPreserved(MkvTrackInfo source, MkvTrackInfo output) =>
        LanguageFieldPreserved(source.Language, output.Language)
        && LanguageFieldPreserved(source.LanguageIetf, output.LanguageIetf);

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

    private static bool AttachmentMetadataEquals(MkvAttachmentInfo expected, MkvAttachmentInfo actual) =>
        string.Equals(expected.FileName, actual.FileName, StringComparison.Ordinal)
        && string.Equals(expected.ContentType, actual.ContentType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.Description, actual.Description, StringComparison.Ordinal)
        && expected.Size == actual.Size
        && string.Equals(expected.Uid, actual.Uid, StringComparison.Ordinal);

    private static bool AddedFontAttachmentMetadataEquals(
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
                        GetFlexibleInt64(properties, "tag_bps")));
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
