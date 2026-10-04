using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class MediaBitrateResolverTests
{
    [Fact]
    public void TrackBitratePrefersMediaInfoValue()
    {
        var inspection = CreateInspection(
            new MkvTrackInfo("video", "V_MPEGH/ISO/HEVC", true, false, null, null, null, Bitrate: 1_500_000));

        var bitrate = MediaBitrateResolver.ResolveTrackBitrate(2_000_000, inspection, "video", 0);

        Assert.Equal(2_000_000, bitrate);
    }

    [Fact]
    public void TrackBitrateFallsBackToMatchingMkvTrackIndex()
    {
        var inspection = CreateInspection(
            new MkvTrackInfo("video", "V_MPEGH/ISO/HEVC", true, false, null, null, null, Bitrate: 1_500_000),
            new MkvTrackInfo("audio", "A_OPUS", true, false, "jpn", "ja", null, Bitrate: 128_000),
            new MkvTrackInfo("audio", "A_OPUS", false, false, "eng", "en", null, Bitrate: 196_121));

        var bitrate = MediaBitrateResolver.ResolveTrackBitrate(null, inspection, "audio", 1);

        Assert.Equal(196_121, bitrate);
    }

    [Fact]
    public void OverallBitratePrefersReportedValue()
    {
        var bitrate = MediaBitrateResolver.ResolveOverallBitrate(
            3_000_000,
            100_000_000,
            1_000_000_000);

        Assert.Equal(3_000_000, bitrate);
    }

    [Fact]
    public void OverallBitrateIsCalculatedFromFileSizeAndDurationWhenMissing()
    {
        var bitrate = MediaBitrateResolver.ResolveOverallBitrate(
            null,
            337_536_614,
            1_373_000_000_000);

        Assert.Equal(1_966_710, bitrate);
    }

    private static MkvInspection CreateInspection(params MkvTrackInfo[] tracks) =>
        new(tracks, [], 0);
}
