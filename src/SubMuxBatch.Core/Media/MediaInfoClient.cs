using System.Globalization;
using MediaInfoLib;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Media;

public sealed record MediaInfoVideoStream(
    string? Id,
    string? StreamOrder,
    string? Format,
    string? FormatProfile,
    string? FormatLevel,
    string? FormatTier,
    string? CodecId,
    string? Title,
    string? Language,
    int? Width,
    int? Height,
    double? DisplayAspectRatio,
    double? PixelAspectRatio,
    double? FrameRate,
    string? FrameRateMode,
    long? Bitrate,
    string? BitrateMode,
    long? MaximumBitrate,
    long? FrameCount,
    long? DurationNanoseconds,
    int? BitDepth,
    string? ColorSpace,
    string? ChromaSubsampling,
    string? ColorRange,
    string? ColorPrimaries,
    string? TransferCharacteristics,
    string? MatrixCoefficients,
    string? HdrFormat,
    string? HdrCompatibility,
    string? ScanType,
    string? ScanOrder,
    bool? Default,
    bool? Forced);

public sealed record MediaInfoAudioStream(
    string? Id,
    string? StreamOrder,
    string? Format,
    string? FormatProfile,
    string? CodecId,
    string? Language,
    string? Title,
    int? Channels,
    string? ChannelLayout,
    double? SamplingRate,
    long? Bitrate,
    string? BitrateMode,
    long? MaximumBitrate,
    long? DurationNanoseconds,
    int? BitDepth,
    string? CompressionMode,
    double? DelayMilliseconds,
    bool? Default,
    bool? Forced);

public sealed record MediaInfoTextStream(
    string? Id,
    string? StreamOrder,
    string? Format,
    string? FormatProfile,
    string? CodecId,
    string? Language,
    string? Title,
    long? DurationNanoseconds,
    long? ElementCount,
    bool? Default,
    bool? Forced);

public sealed record MediaInfoMetadataTag(string Name, string Value);

public sealed record MediaInfoInspection(
    string? ContainerFormat,
    string? ContainerProfile,
    string? ContainerVersion,
    long? DurationNanoseconds,
    long? FileSizeBytes,
    long? OverallBitrate,
    string? OverallBitrateMode,
    string? WritingApplication,
    string? WritingLibrary,
    string? EncodedDate,
    string? TaggedDate,
    IReadOnlyList<MediaInfoVideoStream> VideoStreams,
    IReadOnlyList<MediaInfoAudioStream> AudioStreams,
    IReadOnlyList<MediaInfoTextStream> TextStreams,
    int MenuCount,
    IReadOnlyList<MediaInfoMetadataTag> MetadataTags,
    string? SubMuxBatchVersion,
    string? Comment,
    bool ProcessedBySubMux);

public sealed class MediaInfoClient
{
    private static readonly HashSet<string> ExactTechnicalGeneralFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Count", "StreamCount", "StreamKind", "StreamOrder", "ID", "UniqueID",
        "VideoCount", "AudioCount", "TextCount", "OtherCount", "ImageCount", "MenuCount",
        "Encoded_Date", "Tagged_Date", "IsStreamable", "InternetMediaType",
        "MD5", "SHA1", "SHA256"
    };

    private static readonly string[] TechnicalGeneralFieldPrefixes =
    [
        "CompleteName", "FolderName", "FileName", "FileExtension", "File_Created_Date",
        "File_Modified_Date", "StreamKind", "StreamOrder", "UniqueID", "ID/String",
        "Format", "CodecID", "FileSize", "Duration", "OverallBitRate",
        "BitRate", "MaximumBitRate", "FrameRate", "FrameCount", "StreamSize", "Source_StreamSize",
        "Source_Duration", "HeaderSize", "DataSize", "FooterSize", "MuxingMode", "Delay",
        "Encoded_Application", "Encoded_Library", "Encoded_OperatingSystem", "BufferSize", "PacketSize",
        "Video_Format", "Video_Codec", "Video_Language", "Audio_Format", "Audio_Codec",
        "Audio_Language", "Text_Format", "Text_Codec", "Text_Language", "Other_Format",
        "Image_Format", "Menu_Format", "Chapters_Pos_", "Attachments", "Cover_Data"
    ];

    public Task<MediaInfoInspection> InspectAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(path, cancellationToken), cancellationToken);

    public MediaInfoInspection Inspect(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var mediaInfo = new MediaInfo();
        mediaInfo.Option("Internet", "No");
        mediaInfo.Option("ParseUnknownExtensions", "1");

        if (mediaInfo.Open(path) == 0)
        {
            throw new InvalidOperationException(CoreText.Get("MediaInfo_OpenFailed", Path.GetFileName(path)));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var videoStreams = Enumerable.Range(0, mediaInfo.Count_Get(StreamKind.Video))
                .Select(index => ReadVideoStream(mediaInfo, index))
                .ToArray();
            var audioStreams = Enumerable.Range(0, mediaInfo.Count_Get(StreamKind.Audio))
                .Select(index => ReadAudioStream(mediaInfo, index))
                .ToArray();
            var textStreams = Enumerable.Range(0, mediaInfo.Count_Get(StreamKind.Text))
                .Select(index => ReadTextStream(mediaInfo, index))
                .ToArray();
            var duration = ParseDuration(Get(mediaInfo, StreamKind.General, 0, "Duration"));
            if (duration is null)
            {
                var streamDuration = videoStreams.Select(static stream => stream.DurationNanoseconds)
                    .Concat(audioStreams.Select(static stream => stream.DurationNanoseconds))
                    .Where(static value => value is > 0)
                    .Select(static value => value!.Value)
                    .DefaultIfEmpty()
                    .Max();
                duration = streamDuration > 0 ? streamDuration : null;
            }

            long? fileSize = ParseLong(Get(mediaInfo, StreamKind.General, 0, "FileSize"));
            if (fileSize is null)
            {
                try
                {
                    fileSize = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    // Display metadata remains useful even when a second size lookup fails.
                }
                catch (UnauthorizedAccessException)
                {
                    // Display metadata remains useful even when a second size lookup fails.
                }
            }

            var subMuxVersion = GetFirst(
                mediaInfo,
                StreamKind.General,
                0,
                SubMuxMetadata.VersionTagName,
                "SubMuxBatchVersion",
                "SubMux Batch Version");
            var comment = NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Comment"));
            var metadataTags = ReadMetadataTags(mediaInfo, comment);

            return new MediaInfoInspection(
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Format")),
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Format_Profile")),
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Format_Version")),
                duration,
                fileSize,
                ParseLong(Get(mediaInfo, StreamKind.General, 0, "OverallBitRate")),
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "OverallBitRate_Mode")),
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Encoded_Application")),
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Encoded_Library")),
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Encoded_Date")),
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Tagged_Date")),
                videoStreams,
                audioStreams,
                textStreams,
                mediaInfo.Count_Get(StreamKind.Menu),
                metadataTags,
                subMuxVersion,
                comment,
                SubMuxMetadata.IsProcessed(subMuxVersion, comment));
        }
        finally
        {
            mediaInfo.Close();
        }
    }

    private static IReadOnlyList<MediaInfoMetadataTag> ReadMetadataTags(
        MediaInfo mediaInfo,
        string? comment)
    {
        var tags = new List<MediaInfoMetadataTag>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = mediaInfo.Count_Get(StreamKind.General, 0);
        for (var index = 0; index < count; index++)
        {
            var name = NullIfWhiteSpace(mediaInfo.Get(
                StreamKind.General,
                0,
                index,
                InfoKind.Name));
            var value = NullIfWhiteSpace(mediaInfo.Get(
                StreamKind.General,
                0,
                index,
                InfoKind.Text));
            if (name is null
                || value is null
                || !IsGeneralMetadataTagName(name)
                || !names.Add(name))
            {
                continue;
            }

            if (name.Equals(SubMuxMetadata.CommentTagName, StringComparison.OrdinalIgnoreCase)
                && SubMuxMetadata.IsProcessed(null, comment))
            {
                continue;
            }

            tags.Add(new MediaInfoMetadataTag(name, value));
        }

        return tags;
    }

    internal static bool IsGeneralMetadataTagName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Equals(SubMuxMetadata.VersionTagName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ExactTechnicalGeneralFields.Contains(name))
        {
            return false;
        }

        return !TechnicalGeneralFieldPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetFirst(
        MediaInfo mediaInfo,
        StreamKind streamKind,
        int streamIndex,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = NullIfWhiteSpace(Get(mediaInfo, streamKind, streamIndex, name));
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static MediaInfoVideoStream ReadVideoStream(MediaInfo mediaInfo, int index) => new(
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "ID")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "StreamOrder")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "Format")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "Format_Profile")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "Format_Level")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "Format_Tier")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "CodecID")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "Title")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "Language")),
        ParseInt(Get(mediaInfo, StreamKind.Video, index, "Width")),
        ParseInt(Get(mediaInfo, StreamKind.Video, index, "Height")),
        ParseDouble(Get(mediaInfo, StreamKind.Video, index, "DisplayAspectRatio")),
        ParseDouble(Get(mediaInfo, StreamKind.Video, index, "PixelAspectRatio")),
        ParseDouble(Get(mediaInfo, StreamKind.Video, index, "FrameRate")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "FrameRate_Mode")),
        ParseLong(Get(mediaInfo, StreamKind.Video, index, "BitRate")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "BitRate_Mode")),
        ParseLong(Get(mediaInfo, StreamKind.Video, index, "BitRate_Maximum")),
        ParseLong(Get(mediaInfo, StreamKind.Video, index, "FrameCount")),
        ParseDuration(Get(mediaInfo, StreamKind.Video, index, "Duration")),
        ParseInt(Get(mediaInfo, StreamKind.Video, index, "BitDepth")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "ColorSpace")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "ChromaSubsampling")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "colour_range")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "colour_primaries")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "transfer_characteristics")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "matrix_coefficients")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "HDR_Format")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "HDR_Format_Compatibility")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "ScanType")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "ScanOrder")),
        ParseBoolean(Get(mediaInfo, StreamKind.Video, index, "Default")),
        ParseBoolean(Get(mediaInfo, StreamKind.Video, index, "Forced")));

    private static MediaInfoAudioStream ReadAudioStream(MediaInfo mediaInfo, int index) => new(
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "ID")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "StreamOrder")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "Format")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "Format_Profile")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "CodecID")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "Language")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "Title")),
        ParseInt(Get(mediaInfo, StreamKind.Audio, index, "Channels")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "ChannelLayout")),
        ParseDouble(Get(mediaInfo, StreamKind.Audio, index, "SamplingRate")),
        ParseLong(Get(mediaInfo, StreamKind.Audio, index, "BitRate")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "BitRate_Mode")),
        ParseLong(Get(mediaInfo, StreamKind.Audio, index, "BitRate_Maximum")),
        ParseDuration(Get(mediaInfo, StreamKind.Audio, index, "Duration")),
        ParseInt(Get(mediaInfo, StreamKind.Audio, index, "BitDepth")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "Compression_Mode")),
        ParseDouble(Get(mediaInfo, StreamKind.Audio, index, "Delay")),
        ParseBoolean(Get(mediaInfo, StreamKind.Audio, index, "Default")),
        ParseBoolean(Get(mediaInfo, StreamKind.Audio, index, "Forced")));

    private static MediaInfoTextStream ReadTextStream(MediaInfo mediaInfo, int index) => new(
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "ID")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "StreamOrder")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "Format")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "Format_Profile")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "CodecID")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "Language")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "Title")),
        ParseDuration(Get(mediaInfo, StreamKind.Text, index, "Duration")),
        ParseLong(Get(mediaInfo, StreamKind.Text, index, "ElementCount")),
        ParseBoolean(Get(mediaInfo, StreamKind.Text, index, "Default")),
        ParseBoolean(Get(mediaInfo, StreamKind.Text, index, "Forced")));

    private static string Get(MediaInfo mediaInfo, StreamKind kind, int index, string parameter) =>
        mediaInfo.Get(kind, index, parameter).Trim();

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ParseInt(string? value)
    {
        var number = ParseDouble(value);
        return number is >= int.MinValue and <= int.MaxValue
            ? (int)Math.Round(number.Value, MidpointRounding.AwayFromZero)
            : null;
    }

    private static long? ParseLong(string? value)
    {
        var number = ParseDouble(value);
        return number is >= long.MinValue and <= long.MaxValue
            ? (long)Math.Round(number.Value, MidpointRounding.AwayFromZero)
            : null;
    }

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var firstValue = value.Split(" / ", 2, StringSplitOptions.TrimEntries)[0];
        return double.TryParse(firstValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
            ? parsed
            : null;
    }

    private static bool? ParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim() switch
        {
            "Yes" or "yes" or "1" => true,
            "No" or "no" or "0" => false,
            _ => null
        };
    }

    private static long? ParseDuration(string? milliseconds)
    {
        var parsed = ParseDouble(milliseconds);
        if (parsed is null || parsed < 0 || parsed > long.MaxValue / 1_000_000d)
        {
            return null;
        }

        return (long)Math.Round(parsed.Value * 1_000_000d, MidpointRounding.AwayFromZero);
    }
}
