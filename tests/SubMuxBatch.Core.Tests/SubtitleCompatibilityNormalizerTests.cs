using System.Text;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class SubtitleCompatibilityNormalizerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SubtitleCompatibilityTests",
        Guid.NewGuid().ToString("N"));

    public SubtitleCompatibilityNormalizerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task NormalizesUppercaseFormattingAndFlattensRubyForAssOnly()
    {
        var source = Path.Combine(_root, "source.srt");
        var output = Path.Combine(_root, "output.srt");
        const string text = """
            1
            00:00:00,000 --> 00:00:01,000
            <FONT COLOR="#FF0000" FACE="Arial" SIZE="42"><B>굵게</B></FONT> <RUBY><RB>漢</RB><RT>かん</RT></RUBY>
            """;
        await File.WriteAllTextAsync(source, text, new UTF8Encoding(false));

        await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, output);

        var normalized = await File.ReadAllTextAsync(output);
        Assert.Contains("<font color=\"#FF0000\" face=\"Arial\" size=\"42\"><b>굵게</b></font>", normalized);
        Assert.Contains("漢(かん)", normalized);
        Assert.DoesNotContain("<ruby", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(text, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task ClampsNegativeTimestampsWithoutExtendingThePositiveEndTime()
    {
        var source = Path.Combine(_root, "negative.srt");
        var output = Path.Combine(_root, "normalized.srt");
        const string text = "1\r\n-00:00:02,000 --> 00:00:05,000\r\nLeading negative timestamp\r\n\r\n"
                            + "2\r\n00:00:-01,250 --> 00:00:03,500\r\nNegative seconds component\r\n\r\n"
                            + "3\r\n00:00:05,440 --> 00:00:05,380\r\nInvalid positive range\r\n";
        await File.WriteAllTextAsync(source, text, new UTF8Encoding(false));

        var adjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeSrtTimestampsAsync(
            source,
            output);

        Assert.Equal(2, adjustments.Count);
        Assert.Equal(2, adjustments[0].LineNumber);
        Assert.Equal("-00:00:02,000 --> 00:00:05,000", adjustments[0].OriginalRange);
        Assert.Equal("00:00:00,000 --> 00:00:05,000", adjustments[0].AdjustedRange);
        Assert.Equal(6, adjustments[1].LineNumber);
        var normalized = await File.ReadAllTextAsync(output);
        Assert.Contains("00:00:00,000 --> 00:00:05,000", normalized);
        Assert.Contains("00:00:00,000 --> 00:00:03,500", normalized);
        Assert.Contains("00:00:05,440 --> 00:00:05,380", normalized);
        Assert.Equal(text, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task HandlesNegativeMillisecondComponentsAndRemovesFullyPrerollCue()
    {
        var source = Path.Combine(_root, "negative-milliseconds.srt");
        var output = Path.Combine(_root, "normalized.srt");
        const string text = "1\r\n"
                            + "00:00:00,-340 --> 00:00:00,-340\r\n"
                            + "Intro\r\n\r\n"
                            + "2\r\n"
                            + "00:00:00,-160 --> 00:00:19,610\r\n"
                            + "뭐든 잘하고 우수\r\n";
        await File.WriteAllTextAsync(source, text, new UTF8Encoding(false));

        var adjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeSrtTimestampsAsync(
            source,
            output);

        Assert.Equal(2, adjustments.Count);
        Assert.Equal(2, adjustments[0].LineNumber);
        Assert.True(adjustments[0].Removed);
        Assert.Equal("00:00:00,-340 --> 00:00:00,-340", adjustments[0].OriginalRange);
        Assert.Equal(6, adjustments[1].LineNumber);
        Assert.False(adjustments[1].Removed);
        Assert.Equal("00:00:00,000 --> 00:00:19,610", adjustments[1].AdjustedRange);

        var normalized = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("Intro", normalized);
        Assert.DoesNotContain(",-", normalized);
        Assert.Contains("00:00:00,000 --> 00:00:19,610", normalized);
        Assert.Contains("뭐든 잘하고 우수", normalized);
        Assert.Equal(text, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task ClampsNegativeAssDialogueTimestampAndPreservesSource()
    {
        var source = Path.Combine(_root, "negative.ass");
        var output = Path.Combine(_root, "normalized.ass");
        const string text = "[Events]\r\n"
                            + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n"
                            + "Dialogue: 0,0:00:-01.00,0:00:05.00,Default,,0,0,0,,Test\r\n";
        await File.WriteAllTextAsync(source, text, new UTF8Encoding(false));

        var adjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeAssTimestampsAsync(
            source,
            output);

        var adjustment = Assert.Single(adjustments);
        Assert.Equal(3, adjustment.LineNumber);
        Assert.Equal("0:00:-01.00 --> 0:00:05.00", adjustment.OriginalRange);
        Assert.Equal("0:00:00.00 --> 0:00:05.00", adjustment.AdjustedRange);
        Assert.Contains(
            "Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,Test",
            await File.ReadAllTextAsync(output));
        Assert.Equal(text, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task ClampsNegativeSmiSyncTimestampAndPreservesSource()
    {
        var source = Path.Combine(_root, "negative.smi");
        var output = Path.Combine(_root, "normalized.smi");
        const string text = "<SAMI>\r\n<BODY>\r\n"
                            + "<SYNC Start=\"-1000\"><P>Test\r\n"
                            + "<SYNC Start=5000><P>&nbsp;\r\n"
                            + "</BODY>\r\n</SAMI>\r\n";
        await File.WriteAllTextAsync(source, text, new UTF8Encoding(false));

        var adjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeSmiTimestampsAsync(
            source,
            output);

        var adjustment = Assert.Single(adjustments);
        Assert.Equal(3, adjustment.LineNumber);
        Assert.Equal("Start=-1000 ms", adjustment.OriginalRange);
        Assert.Equal("Start=0 ms", adjustment.AdjustedRange);
        Assert.Contains("<SYNC Start=\"0\"><P>Test", await File.ReadAllTextAsync(output));
        Assert.Equal(text, await File.ReadAllTextAsync(source));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
