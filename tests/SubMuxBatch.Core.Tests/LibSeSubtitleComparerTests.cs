using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class LibSeSubtitleComparerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        nameof(LibSeSubtitleComparerTests),
        Guid.NewGuid().ToString("N"));

    public LibSeSubtitleComparerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task MatchesAssAndSrtByLibSePlainTextRegardlessOfFileOrderAndFormatting()
    {
        var ass = await WriteAsync("source.ass", """
            [Script Info]
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,{\b1}세 번째 자막{\b0}
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,첫째\N둘째
            Dialogue: 0,0:00:02.00,0:00:03.00,Default,,0,0,0,,{\p1}m 0 0 l 10 10{\p0}
            """);
        var srt = await WriteAsync("source.srt", """
            1
            00:00:01,000 --> 00:00:02,000
            첫째
            둘째

            2
            00:00:03,000 --> 00:00:04,000
            세 번째 자막
            """);

        Assert.True(await LibSeSubtitleComparer.AreEquivalentAsync(ass, srt));
    }

    [Fact]
    public async Task MatchesSrtAfterContainerSortingRenumberingAndEmptyCueRemoval()
    {
        var input = await WriteAsync("input.srt", """
            7
            00:00:03,000 --> 00:00:04,000
            세 번째 자막

            8
            00:00:01,000 --> 00:00:02,000
             첫 번째 자막

            9
            00:00:02,000 --> 00:00:03,000

            """);
        var extracted = await WriteAsync("extracted.srt", """
            1
            00:00:01,000 --> 00:00:02,000
            첫 번째 자막

            2
            00:00:03,000 --> 00:00:04,000
            세 번째 자막
            """);

        Assert.True(await LibSeSubtitleComparer.AreEquivalentAsync(input, extracted));
    }

    [Theory]
    [InlineData("다른 자막", "00:00:01,000 --> 00:00:02,000")]
    [InlineData("같은 자막", "00:00:03,000 --> 00:00:04,000")]
    public async Task RejectsDifferentPlainTextOrTiming(string text, string timecode)
    {
        var left = await WriteAsync("left.srt", "1\n00:00:01,000 --> 00:00:02,000\n같은 자막\n");
        var right = await WriteAsync("right.srt", $"1\n{timecode}\n{text}\n");

        Assert.False(await LibSeSubtitleComparer.AreEquivalentAsync(left, right));
    }

    private async Task<string> WriteAsync(string name, string text)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
