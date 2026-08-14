using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class AssInlineStylePostProcessorTests
{
    [Fact]
    public void RestoresDroppedPositionTagsWithoutScalingInlineValues()
    {
        const string srt = """
            1
            00:00:00,000 --> 00:00:01,000
            {\an8}<font size="40">Top</font>

            2
            00:00:01,000 --> 00:00:02,000
            {\pos(320,72)}<font size="30">Positioned</font>

            3
            00:00:02,000 --> 00:00:03,000
            {\move(100,120,500,600,100,900)}<font size="36">Moving</font>
            """;
        const string ass = """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\an8\fs40}Top
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\fs30}Positioned
            Dialogue: 0,0:00:02.00,0:00:03.00,Default,,0,0,0,,{\fs36}Moving
            """;

        var result = AssInlineStylePostProcessor.Apply(ass, srt);

        Assert.Contains(@"{\an8\fs40}Top", result);
        Assert.Contains(@"{\fs30\pos(320,72)}Positioned", result);
        Assert.Contains(@"{\fs36\move(100,120,500,600,100,900)}Moving", result);
        Assert.Equal(1, Count(result, @"\an8"));
    }

    [Fact]
    public void PreservesExistingPositionAndDialogueMarginsWithoutDuplicatingTags()
    {
        const string srt = """
            1
            00:00:00,000 --> 00:00:01,000
            {\an7\pos(100,200)}Text
            """;
        const string ass = """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,20,30,40,,{\an7\pos(100,200)\fs20}Text
            """;

        var result = AssInlineStylePostProcessor.Apply(ass, srt);

        Assert.Contains("Default,,20,30,40,,", result);
        Assert.Contains(@"{\an7\pos(100,200)\fs20}Text", result);
        Assert.Equal(1, Count(result, @"\pos("));
        Assert.Equal(1, Count(result, @"\an7"));
    }

    [Fact]
    public void RestoresOrgAndMatchesOneCentisecondRoundingDrift()
    {
        const string srt = """
            1
            00:00:00,995 --> 00:00:02,005
            {\an9\org(640,360)}Text
            """;
        const string ass = """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.01,Default,,0,0,0,,{\an9}Text
            """;

        var result = AssInlineStylePostProcessor.Apply(ass, srt);

        Assert.Contains(@"{\an9\org(640,360)}Text", result);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ParsesCueBlocksWithLfAndCrLf(string newLine)
    {
        var srt = string.Join(newLine,
            "1",
            "00:00:00,000 --> 00:00:01,000",
            @"{\pos(320,72)}First",
            string.Empty,
            "2",
            "00:00:01,000 --> 00:00:02,000",
            @"{\move(10,20,30,40)}Second");
        const string ass = """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,First
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Second
            """;

        var result = AssInlineStylePostProcessor.Apply(ass, srt);

        Assert.Contains(@"{\pos(320,72)}First", result);
        Assert.Contains(@"{\move(10,20,30,40)}Second", result);
    }

    [Fact]
    public void RejectsNullInputs()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AssInlineStylePostProcessor.Apply(null!, string.Empty));
        Assert.Throws<ArgumentNullException>(() =>
            AssInlineStylePostProcessor.Apply(string.Empty, null!));
    }

    private static int Count(string value, string pattern) =>
        (value.Length - value.Replace(pattern, string.Empty, StringComparison.Ordinal).Length) / pattern.Length;
}
