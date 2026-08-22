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

        Assert.Equal(3, adjustments.Count);
        Assert.Equal(2, adjustments[0].LineNumber);
        Assert.Equal("-00:00:02,000 --> 00:00:05,000", adjustments[0].OriginalRange);
        Assert.Equal("00:00:00,000 --> 00:00:05,000", adjustments[0].AdjustedRange);
        Assert.Equal(6, adjustments[1].LineNumber);
        Assert.Equal(10, adjustments[2].LineNumber);
        Assert.Equal(SubtitleTimestampAdjustmentKind.RemovedInvalidRange, adjustments[2].Kind);
        var normalized = await File.ReadAllTextAsync(output);
        Assert.Contains("00:00:00,000 --> 00:00:05,000", normalized);
        Assert.Contains("00:00:00,000 --> 00:00:03,500", normalized);
        Assert.DoesNotContain("00:00:05,440 --> 00:00:05,380", normalized);
        Assert.DoesNotContain("Invalid positive range", normalized);
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
    public async Task ParsesCuesWithoutDependingOnSequenceNumbersOrFixedNegativeWidths()
    {
        var source = Path.Combine(_root, "manual.srt");
        var output = Path.Combine(_root, "normalized.srt");
        const string text = "5\r\n"
                            + "00:00:-3,-900 --> 00:00:00,-140\r\n"
                            + "등장인물들은 모두 18세 이상입니다\r\n"
                            + "5\r\n"
                            + "00:00:00,-90 --> 00:00:03,340\r\n"
                            + "가져가 주세요\r\n"
                            + "00:00:09,370 --> 00:00:13,280\r\n"
                            + "번호 없는 첫 줄\r\n"
                            + "번호 없는 둘째 줄\r\n";
        await File.WriteAllTextAsync(source, text, new UTF8Encoding(false));

        var adjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeSrtTimestampsAsync(
            source,
            output);

        Assert.Equal(2, adjustments.Count);
        Assert.Equal(SubtitleTimestampAdjustmentKind.RemovedBeforeVideoStart, adjustments[0].Kind);
        Assert.Equal("00:00:-3,-900 --> 00:00:00,-140", adjustments[0].OriginalRange);
        Assert.Equal("00:00:00,000 --> 00:00:03,340", adjustments[1].AdjustedRange);

        var normalized = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("등장인물들은", normalized);
        Assert.DoesNotContain("00:00:-", normalized);
        Assert.DoesNotContain(",-", normalized);
        Assert.Contains("1\r\n00:00:00,000 --> 00:00:03,340", normalized);
        Assert.Contains("2\r\n00:00:09,370 --> 00:00:13,280", normalized);
        Assert.Contains("번호 없는 첫 줄\r\n번호 없는 둘째 줄", normalized);
        Assert.Equal(text, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task RemovesUnparseableSrtTimestampCueInsteadOfPassingItToMkvmerge()
    {
        var source = Path.Combine(_root, "invalid-timestamp.srt");
        var output = Path.Combine(_root, "normalized.srt");
        const string text = "20\r\n"
                            + "00:00:XX,000 --> 00:00:03,000\r\n"
                            + "Invalid timestamp\r\n\r\n"
                            + "20\r\n"
                            + "00:00:04,000 --> 00:00:05,000\r\n"
                            + "Valid timestamp\r\n";
        await File.WriteAllTextAsync(source, text, new UTF8Encoding(false));

        var adjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeSrtTimestampsAsync(
            source,
            output);

        var adjustment = Assert.Single(adjustments);
        Assert.Equal(SubtitleTimestampAdjustmentKind.RemovedInvalidTimestamp, adjustment.Kind);
        var normalized = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("Invalid timestamp", normalized);
        Assert.Contains("1\r\n00:00:04,000 --> 00:00:05,000", normalized);
        Assert.Contains("Valid timestamp", normalized);
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
    public async Task RemovesInvalidAssRangesButKeepsValidDialogues()
    {
        var source = Path.Combine(_root, "invalid-ranges.ass");
        var output = Path.Combine(_root, "normalized.ass");
        const string text = "[Events]\r\n"
                            + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n"
                            + "Dialogue: 0,0:00:05.44,0:00:05.38,Default,,0,0,0,,Reversed\r\n"
                            + "Dialogue: 0,bad,0:00:06.00,Default,,0,0,0,,Invalid\r\n"
                            + "Dialogue: 0,0:00:07.00,0:00:08.00,Default,,0,0,0,,Valid\r\n";
        await File.WriteAllTextAsync(source, text, new UTF8Encoding(false));

        var adjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeAssTimestampsAsync(
            source,
            output);

        Assert.Equal(2, adjustments.Count);
        Assert.Equal(SubtitleTimestampAdjustmentKind.RemovedInvalidRange, adjustments[0].Kind);
        Assert.Equal(SubtitleTimestampAdjustmentKind.RemovedInvalidTimestamp, adjustments[1].Kind);
        var normalized = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("Reversed", normalized);
        Assert.DoesNotContain("Invalid", normalized);
        Assert.Contains("Dialogue: 0,0:00:07.00,0:00:08.00,Default,,0,0,0,,Valid", normalized);
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
