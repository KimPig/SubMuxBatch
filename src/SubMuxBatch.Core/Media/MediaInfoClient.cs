using System.Globalization;
using MediaInfoLib;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Media;

public sealed record MediaInfoVideoStream(
    string? Format,
    string? FormatProfile,
    string? CodecId,
    int? Width,
    int? Height,
    double? FrameRate,
    string? FrameRateMode,
    long? Bitrate,
    long? FrameCount,
    long? DurationNanoseconds,
    int? BitDepth,
    string? ScanType);

public sealed record MediaInfoAudioStream(
    string? Format,
    string? FormatProfile,
    string? CodecId,
    string? Language,
    string? Title,
    int? Channels,
    string? ChannelLayout,
    double? SamplingRate,
    long? Bitrate,
    long? DurationNanoseconds,
    int? BitDepth);

public sealed record MediaInfoTextStream(
    string? Format,
    string? CodecId,
    string? Language,
    string? Title);

public sealed record MediaInfoInspection(
    string? ContainerFormat,
    string? ContainerProfile,
    long? DurationNanoseconds,
    long? FileSizeBytes,
    long? OverallBitrate,
    IReadOnlyList<MediaInfoVideoStream> VideoStreams,
    IReadOnlyList<MediaInfoAudioStream> AudioStreams,
    IReadOnlyList<MediaInfoTextStream> TextStreams,
    int MenuCount);

public sealed class MediaInfoClient
{
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

            return new MediaInfoInspection(
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Format")),
                NullIfWhiteSpace(Get(mediaInfo, StreamKind.General, 0, "Format_Profile")),
                duration,
                fileSize,
                ParseLong(Get(mediaInfo, StreamKind.General, 0, "OverallBitRate")),
                videoStreams,
                audioStreams,
                textStreams,
                mediaInfo.Count_Get(StreamKind.Menu));
        }
        finally
        {
            mediaInfo.Close();
        }
    }

    private static MediaInfoVideoStream ReadVideoStream(MediaInfo mediaInfo, int index) => new(
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "Format")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "Format_Profile")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "CodecID")),
        ParseInt(Get(mediaInfo, StreamKind.Video, index, "Width")),
        ParseInt(Get(mediaInfo, StreamKind.Video, index, "Height")),
        ParseDouble(Get(mediaInfo, StreamKind.Video, index, "FrameRate")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "FrameRate_Mode")),
        ParseLong(Get(mediaInfo, StreamKind.Video, index, "BitRate")),
        ParseLong(Get(mediaInfo, StreamKind.Video, index, "FrameCount")),
        ParseDuration(Get(mediaInfo, StreamKind.Video, index, "Duration")),
        ParseInt(Get(mediaInfo, StreamKind.Video, index, "BitDepth")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Video, index, "ScanType")));

    private static MediaInfoAudioStream ReadAudioStream(MediaInfo mediaInfo, int index) => new(
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "Format")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "Format_Profile")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "CodecID")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "Language")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "Title")),
        ParseInt(Get(mediaInfo, StreamKind.Audio, index, "Channels")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Audio, index, "ChannelLayout")),
        ParseDouble(Get(mediaInfo, StreamKind.Audio, index, "SamplingRate")),
        ParseLong(Get(mediaInfo, StreamKind.Audio, index, "BitRate")),
        ParseDuration(Get(mediaInfo, StreamKind.Audio, index, "Duration")),
        ParseInt(Get(mediaInfo, StreamKind.Audio, index, "BitDepth")));

    private static MediaInfoTextStream ReadTextStream(MediaInfo mediaInfo, int index) => new(
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "Format")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "CodecID")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "Language")),
        NullIfWhiteSpace(Get(mediaInfo, StreamKind.Text, index, "Title")));

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
