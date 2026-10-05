using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class FfmpegPacketSizeAnalyzerTests
{
    [Theory]
    [InlineData("#media_type 0: video", 0, "video")]
    [InlineData("#media_type 12: subtitle", 12, "subtitle")]
    public void MediaTypeHeaderIsParsed(string line, int expectedIndex, string expectedType)
    {
        Assert.True(FfmpegPacketSizeAnalyzer.TryParseMediaTypeLine(line, out var index, out var type));
        Assert.Equal(expectedIndex, index);
        Assert.Equal(expectedType, type);
    }

    [Theory]
    [InlineData("0,       -312,       -312,      960,        3, 8abe71cf", 0, 3)]
    [InlineData("2, 1000, 1000, 40, 23578, deadbeef, S=1, 10, aabbccdd", 2, 23578)]
    public void FrameHashPacketSizeIsParsed(string line, int expectedIndex, long expectedBytes)
    {
        Assert.True(FfmpegPacketSizeAnalyzer.TryParseFrameHashLine(line, out var index, out var bytes));
        Assert.Equal(expectedIndex, index);
        Assert.Equal(expectedBytes, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#stream#, dts, pts, duration, size, hash")]
    [InlineData("invalid")]
    public void NonPacketLinesAreIgnored(string line) =>
        Assert.False(FfmpegPacketSizeAnalyzer.TryParseFrameHashLine(line, out _, out _));
}
