using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class MediaSizeResolverTests
{
    [Fact]
    public void TrackSizePrefersMkvStatisticsTag()
    {
        var inspection = CreateInspection(
            new MkvTrackInfo(
                "audio", "A_OPUS", true, false, "jpn", "ja", null,
                SizeBytes: 20_000_000));

        var result = MediaSizeResolver.ResolveTrackSize(
            21_000_000,
            inspection,
            "audio",
            0,
            128_000,
            1_000_000_000_000);

        Assert.Equal(20_000_000, result.Bytes);
        Assert.True(result.IsExact);
    }

    [Fact]
    public void TrackSizeFallsBackToMkvStatisticsTagWhenMediaInfoIsMissing()
    {
        var inspection = CreateInspection(
            new MkvTrackInfo(
                "audio", "A_OPUS", true, false, "jpn", "ja", null,
                SizeBytes: 20_000_000));

        var result = MediaSizeResolver.ResolveTrackSize(
            null,
            inspection,
            "audio",
            0,
            128_000,
            1_000_000_000_000);

        Assert.Equal(20_000_000, result.Bytes);
        Assert.True(result.IsExact);
    }

    [Fact]
    public void TrackSizeUsesMediaInfoWhenMkvStatisticsTagIsMissing()
    {
        var inspection = CreateInspection(
            new MkvTrackInfo("audio", "A_OPUS", true, false, "jpn", "ja", null));

        var result = MediaSizeResolver.ResolveTrackSize(
            21_000_000,
            inspection,
            "audio",
            0,
            128_000,
            1_000_000_000_000);

        Assert.Equal(21_000_000, result.Bytes);
        Assert.True(result.IsExact);
    }

    [Fact]
    public void TrackSizeIsEstimatedFromBitrateAndDurationWhenExactSizeIsMissing()
    {
        var result = MediaSizeResolver.ResolveTrackSize(
            null,
            null,
            "audio",
            0,
            160_000,
            1_200_000_000_000);

        Assert.Equal(24_000_000, result.Bytes);
        Assert.True(result.IsEstimated);
    }

    [Fact]
    public void TrackSizeRemainsUnknownWithoutSizeOrEstimateInputs()
    {
        var result = MediaSizeResolver.ResolveTrackSize(null, null, "subtitles", 0, null, null);

        Assert.Null(result.Bytes);
        Assert.False(result.IsEstimated);
    }

    private static MkvInspection CreateInspection(params MkvTrackInfo[] tracks) =>
        new(tracks, [], 0);
}
