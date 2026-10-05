using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Media;

public sealed record ResolvedMediaSize(long? Bytes, bool IsEstimated)
{
    public bool IsKnown => Bytes is >= 0;
    public bool IsExact => IsKnown && !IsEstimated;
}

public static class MediaSizeResolver
{
    public static ResolvedMediaSize ResolveTrackSize(
        long? mediaInfoSize,
        MkvInspection? mkvInspection,
        string trackType,
        int trackIndex,
        long? bitrate,
        long? durationNanoseconds)
    {
        if (mkvInspection is not null && trackIndex >= 0)
        {
            var mkvSize = mkvInspection.Tracks
                .Where(track => string.Equals(track.Type, trackType, StringComparison.OrdinalIgnoreCase))
                .ElementAtOrDefault(trackIndex)
                ?.SizeBytes;
            if (mkvSize is >= 0)
            {
                return new ResolvedMediaSize(mkvSize, false);
            }
        }

        if (mediaInfoSize is >= 0)
        {
            return new ResolvedMediaSize(mediaInfoSize, false);
        }

        if (bitrate is > 0 && durationNanoseconds is > 0)
        {
            var estimated = (double)bitrate.Value * durationNanoseconds.Value / 8_000_000_000d;
            if (double.IsFinite(estimated) && estimated is >= 0 and <= long.MaxValue)
            {
                return new ResolvedMediaSize(
                    (long)Math.Round(estimated, MidpointRounding.AwayFromZero),
                    true);
            }
        }

        return new ResolvedMediaSize(null, false);
    }
}
