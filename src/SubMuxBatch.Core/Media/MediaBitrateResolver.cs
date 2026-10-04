using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Media;

public static class MediaBitrateResolver
{
    public static long? ResolveTrackBitrate(
        long? mediaInfoBitrate,
        MkvInspection? mkvInspection,
        string trackType,
        int trackIndex)
    {
        if (mediaInfoBitrate is > 0)
        {
            return mediaInfoBitrate;
        }

        if (mkvInspection is null || trackIndex < 0)
        {
            return null;
        }

        var bitrate = mkvInspection.Tracks
            .Where(track => string.Equals(track.Type, trackType, StringComparison.OrdinalIgnoreCase))
            .ElementAtOrDefault(trackIndex)
            ?.Bitrate;
        return bitrate is > 0 ? bitrate : null;
    }

    public static long? ResolveOverallBitrate(
        MediaInfoInspection? mediaInfo,
        MkvInspection? mkvInspection) =>
        ResolveOverallBitrate(
            mediaInfo?.OverallBitrate,
            mediaInfo?.FileSizeBytes ?? mkvInspection?.FileSizeBytes,
            mediaInfo?.DurationNanoseconds ?? mkvInspection?.DurationNanoseconds);

    public static long? ResolveOverallBitrate(
        long? reportedBitrate,
        long? fileSizeBytes,
        long? durationNanoseconds)
    {
        if (reportedBitrate is > 0)
        {
            return reportedBitrate;
        }

        if (fileSizeBytes is not > 0 || durationNanoseconds is not > 0)
        {
            return null;
        }

        var calculated = fileSizeBytes.Value * 8_000_000_000d / durationNanoseconds.Value;
        return double.IsFinite(calculated) && calculated is > 0 and <= long.MaxValue
            ? (long)Math.Round(calculated, MidpointRounding.AwayFromZero)
            : null;
    }
}
