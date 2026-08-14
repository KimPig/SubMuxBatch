using System.Text.Json;
using System.Text.RegularExpressions;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Domain;

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
            new ProcessRequest(executablePath, ["-J", path], Path.GetDirectoryName(path)),
            onOutput,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode >= 2)
        {
            throw new InvalidOperationException(
                $"mkvmerge가 영상 정보를 읽지 못했습니다. (종료 코드 {result.ExitCode}){Environment.NewLine}{result.StandardError.Trim()}");
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
        AudioTrackLanguage? keepOnlyAudioLanguage = null)
    {
        var arguments = new List<string>
        {
            "--gui-mode",
            "-o",
            outputPath
        };

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
                    throw new InvalidOperationException("기존 자막 트랙 ID를 확인할 수 없어 기본 플래그를 해제할 수 없습니다.");
                }

                arguments.Add("--default-track-flag");
                arguments.Add($"{track.Id}:no");
            }
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
                            "폰트가 아닌 기존 첨부파일의 ID를 확인할 수 없어 안전하게 병합할 수 없습니다."))
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
                        $"원본 영상에서 선택한 {GetAudioLanguageDisplayName(audioLanguage)} 오디오 트랙을 찾지 못했습니다. 무음 결과 파일을 만들지 않고 해당 작업은 건너뜁니다.");
                }

                var retainedAudioIds = retainedAudioTracks
                    .Select(static track => track.Id
                        ?? throw new InvalidOperationException(
                            "유지할 오디오 트랙의 ID를 확인할 수 없어 안전하게 병합할 수 없습니다."))
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

        AddSubtitle(arguments, assPath, "스타일 자막 (ASS)", isDefault: true);
        AddSubtitle(arguments, srtPath, "일반 자막 (SRT)", isDefault: false);

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
                $"mkvmerge 병합 실패 (종료 코드 {result.ExitCode}){Environment.NewLine}{details.Trim()}");
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
            warnings.Add("mkvmerge가 경고와 함께 완료되었지만 상세 내용을 제공하지 않았습니다.");
        }

        return warnings;
    }

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
        AudioTrackLanguage? keepOnlyAudioLanguage = null)
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
            errors.Add($"원본 영상에 선택한 {GetAudioLanguageDisplayName(keepOnlyAudioLanguage!.Value)} 오디오 트랙이 없습니다.");
        }

        if (sourceMediaTracks.Length != outputMediaTracks.Length)
        {
            errors.Add("원본과 결과의 비자막 트랙 수가 다릅니다.");
        }
        else
        {
            for (var index = 0; index < sourceMediaTracks.Length; index++)
            {
                if (!string.Equals(sourceMediaTracks[index].Type, outputMediaTracks[index].Type, StringComparison.OrdinalIgnoreCase)
                    || !CodecMetadataEquals(sourceMediaTracks[index], outputMediaTracks[index]))
                {
                    errors.Add($"원본의 {index + 1}번째 비자막 트랙이 결과와 다릅니다.");
                }
                else if (audioFilterApplies
                         && IsTrackType(sourceMediaTracks[index], "audio")
                         && !MatchesAudioLanguage(outputMediaTracks[index], keepOnlyAudioLanguage!.Value))
                {
                    errors.Add(
                        $"결과의 {index + 1}번째 비자막 트랙이 선택한 {GetAudioLanguageDisplayName(keepOnlyAudioLanguage.Value)} 오디오가 아닙니다.");
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
                        $"원본의 {index + 1}번째 선택 오디오 트랙 메타데이터가 결과에 보존되지 않았습니다.");
                }
            }
        }

        var expectedAttachments = removeExistingFontAttachments
            ? source.Attachments.Where(static attachment => !IsFontAttachment(attachment)).ToArray()
            : source.Attachments.ToArray();
        if (expectedAttachments.Length != output.AttachmentCount)
        {
            errors.Add(removeExistingFontAttachments
                ? "폰트를 제외한 원본 첨부 파일 수가 결과와 다릅니다."
                : "원본 첨부 파일 수가 보존되지 않았습니다.");
        }
        else
        {
            for (var index = 0; index < expectedAttachments.Length; index++)
            {
                if (!AttachmentMetadataEquals(expectedAttachments[index], output.Attachments[index]))
                {
                    errors.Add($"보존 대상인 {index + 1}번째 첨부 파일 정보가 결과와 다릅니다.");
                }
            }
        }

        if (source.ChapterCount.HasValue && output.ChapterCount.HasValue
            && source.ChapterCount.Value != output.ChapterCount.Value)
        {
            errors.Add("원본 챕터 수가 보존되지 않았습니다.");
        }

        var sourceSubtitles = source.Tracks.Where(static track => track.Type == "subtitles").ToArray();
        var outputSubtitles = output.Tracks.Where(static track => track.Type == "subtitles").ToArray();
        var expectedSubtitleCount = removeExistingSubtitles ? 2 : sourceSubtitles.Length + 2;
        if (outputSubtitles.Length != expectedSubtitleCount)
        {
            errors.Add($"결과의 자막 트랙은 {expectedSubtitleCount}개여야 하지만 {outputSubtitles.Length}개입니다.");
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
        ValidateSubtitle(outputSubtitles[addedAssIndex], "S_TEXT/ASS", shouldBeDefault: true, "추가된 ASS", errors);
        ValidateSubtitle(outputSubtitles[addedSrtIndex], "S_TEXT/UTF8", shouldBeDefault: false, "추가된 SRT", errors);

        if (outputSubtitles.Count(static track => track.DefaultTrack) != 1)
        {
            errors.Add("추가된 ASS만 유일한 기본 자막 트랙이어야 합니다.");
        }

        return errors;
    }

    private static void ValidatePreservedSubtitle(
        MkvTrackInfo source,
        MkvTrackInfo output,
        int index,
        ICollection<string> errors)
    {
        var label = $"기존 {index + 1}번째 자막";
        if (!CodecMetadataEquals(source, output))
        {
            errors.Add($"{label} 트랙의 코덱이 보존되지 않았습니다: {source.CodecId} -> {output.CodecId}");
        }

        if (source.ForcedTrack != output.ForcedTrack)
        {
            errors.Add($"{label} 트랙의 forced 플래그가 보존되지 않았습니다.");
        }

        if (!LanguageMetadataPreserved(source, output))
        {
            errors.Add($"{label} 트랙의 언어 정보가 보존되지 않았습니다.");
        }

        if (!string.Equals(source.TrackName, output.TrackName, StringComparison.Ordinal))
        {
            errors.Add($"{label} 트랙 이름이 보존되지 않았습니다.");
        }

        if (output.DefaultTrack)
        {
            errors.Add($"{label} 트랙의 기본 플래그가 해제되지 않았습니다.");
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

    private static void ValidateSubtitle(
        MkvTrackInfo track,
        string expectedCodec,
        bool shouldBeDefault,
        string label,
        ICollection<string> errors)
    {
        if (!string.Equals(track.CodecId, expectedCodec, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{label} 트랙 코덱이 {expectedCodec}이 아닙니다: {track.CodecId}");
        }

        if (track.DefaultTrack != shouldBeDefault)
        {
            errors.Add($"{label} 트랙의 기본 플래그가 올바르지 않습니다.");
        }

        if (track.ForcedTrack)
        {
            errors.Add($"{label} 트랙에 forced 플래그가 설정되어 있습니다.");
        }

        var isKorean = string.Equals(track.Language, "kor", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(track.Language, "ko", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(track.LanguageIetf, "ko", StringComparison.OrdinalIgnoreCase)
                       || track.LanguageIetf?.StartsWith("ko-", StringComparison.OrdinalIgnoreCase) == true;
        if (!isKorean)
        {
            errors.Add($"{label} 트랙의 언어가 한국어로 지정되지 않았습니다.");
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
        AudioTrackLanguage.English => "영어",
        AudioTrackLanguage.Japanese => "일본어",
        AudioTrackLanguage.Korean => "한국어",
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
            throw new InvalidOperationException("mkvmerge JSON 정보를 해석할 수 없습니다.", exception);
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
