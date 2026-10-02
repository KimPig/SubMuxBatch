using System.Text;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class LibSeSubtitleConverterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        nameof(LibSeSubtitleConverterTests),
        Guid.NewGuid().ToString("N"));

    public LibSeSubtitleConverterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ConvertsAssToSrtWithoutLeakingAssEventFields()
    {
        var input = Path.Combine(_root, "source.ass");
        var output = Path.Combine(_root, "result.srt");
        await File.WriteAllTextAsync(input, """
            [Script Info]
            ScriptType: v4.00

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\i1}Hello{\i0}
            """);

        var result = await new LibSeSubtitleConverter().ConvertAsync(
            input,
            output,
            SubtitleOutputFormat.SubRip);

        var text = await File.ReadAllTextAsync(output);
        Assert.Equal(1, result.CueCount);
        Assert.Contains("Hello", text);
        Assert.DoesNotContain("Default,,0,0,0,,", text);
    }

    [Fact]
    public async Task AssToSrtMatchesSubtitleEditGuiSaveAsFormattingPolicy()
    {
        var input = Path.Combine(_root, "formatted.ass");
        var output = Path.Combine(_root, "formatted.srt");
        await File.WriteAllTextAsync(input, """
            [Script Info]
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Comment: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Editor note
            Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\an5\move(1,2,3,4)\fad(100,100)\fnGaramond\fs40\b1}Visible
            Dialogue: 0,0:00:04.00,0:00:05.00,Default,,0,0,0,,{\p1}m 0 0 l 10 10{\p0}
            """);

        await new LibSeSubtitleConverter().ConvertAsync(
            input,
            output,
            SubtitleOutputFormat.SubRip);

        var text = await File.ReadAllTextAsync(output);
        Assert.Contains(@"{\an5}Visible", text);
        Assert.DoesNotContain("Editor note", text);
        Assert.DoesNotContain(@"\move", text);
        Assert.DoesNotContain(@"\fnGaramond", text);
        Assert.DoesNotContain("m 0 0 l 10 10", text);
    }

    [Fact]
    public async Task ConvertsCp949SmiToSrt()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var input = Path.Combine(_root, "source.smi");
        var output = Path.Combine(_root, "result.srt");
        const string smi = "<SAMI><BODY><SYNC Start=0><P Class=KRCC>안녕하세요<SYNC Start=1000><P Class=KRCC>&nbsp;</BODY></SAMI>";
        await File.WriteAllTextAsync(input, smi, Encoding.GetEncoding(949));

        var result = await new LibSeSubtitleConverter().ConvertAsync(
            input,
            output,
            SubtitleOutputFormat.SubRip);

        var text = await File.ReadAllTextAsync(output);
        Assert.True(result.CueCount >= 1);
        Assert.Contains("안녕하세요", text);
        Assert.Contains("00:00:00,000 --> 00:00:01,000", text);
    }

    [Fact]
    public async Task ConvertsSrtToAss()
    {
        var input = Path.Combine(_root, "source.srt");
        var output = Path.Combine(_root, "result.ass");
        await File.WriteAllTextAsync(input, """
            1
            00:00:01,000 --> 00:00:03,000
            <i>Hello</i>
            """);

        var result = await new LibSeSubtitleConverter().ConvertAsync(
            input,
            output,
            SubtitleOutputFormat.AdvancedSubStationAlpha);

        var text = await File.ReadAllTextAsync(output);
        Assert.Equal(1, result.CueCount);
        Assert.Contains("[V4+ Styles]", text);
        Assert.Contains("[Events]", text);
        Assert.Contains("Dialogue:", text);
        Assert.Contains("Hello", text);
    }

    [Fact]
    public async Task ConvertsSrtToAssWithConfiguredHeaderAndSingleEventsFormat()
    {
        var input = Path.Combine(_root, "styled.srt");
        var style = Path.Combine(_root, "style.ass");
        var output = Path.Combine(_root, "styled.ass");
        await File.WriteAllTextAsync(input, "1\n00:00:01,000 --> 00:00:03,000\nHello\n");
        await File.WriteAllTextAsync(style, AssStyleTemplateWriter.CreateHeader(
            new AppSettings
            {
                PlayResX = 1920,
                PlayResY = 1080,
                AssStyleLine = AppSettings.DefaultAssStyleLine.Replace(
                    "SubMux Sans",
                    "Test Family",
                    StringComparison.Ordinal)
            }));

        await new LibSeSubtitleConverter().ConvertAsync(
            input,
            output,
            SubtitleOutputFormat.AdvancedSubStationAlpha,
            style,
            1920,
            1080);

        var text = await File.ReadAllTextAsync(output);
        Assert.Contains("PlayResX: 1920", text);
        Assert.Contains("PlayResY: 1080", text);
        Assert.Contains("Style: Default,Test Family", text);
        Assert.Equal(
            1,
            text.Split('\n').Count(static line =>
                line.StartsWith("Format: Layer, Start, End", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CancellationDoesNotLeavePartialOutput()
    {
        var input = Path.Combine(_root, "cancel.srt");
        var output = Path.Combine(_root, "cancel.ass");
        await File.WriteAllTextAsync(input, "1\n00:00:01,000 --> 00:00:03,000\nHello\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LibSeSubtitleConverter().ConvertAsync(
                input,
                output,
                SubtitleOutputFormat.AdvancedSubStationAlpha,
                cancellationToken: cancellation.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_root, ".cancel.ass.*.tmp"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MatchesSubtitleEditGuiSrtWhenComparisonFilesAreProvided()
    {
        var assPath = Environment.GetEnvironmentVariable("SUBMUX_GUI_SAMPLE_ASS");
        var expectedSrtPath = Environment.GetEnvironmentVariable("SUBMUX_GUI_SAMPLE_SRT");
        if (string.IsNullOrWhiteSpace(assPath)
            || string.IsNullOrWhiteSpace(expectedSrtPath)
            || !File.Exists(assPath)
            || !File.Exists(expectedSrtPath))
        {
            return;
        }

        var output = Path.Combine(_root, "gui-comparison.srt");
        await new LibSeSubtitleConverter().ConvertAsync(
            assPath,
            output,
            SubtitleOutputFormat.SubRip);

        static string Normalize(string text) => text
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd();

        Assert.Equal(
            Normalize(await File.ReadAllTextAsync(expectedSrtPath)),
            Normalize(await File.ReadAllTextAsync(output)));
    }

    [Fact]
    public async Task ConcurrentConversionsDoNotMixSubtitleContents()
    {
        var converter = new LibSeSubtitleConverter();
        var tasks = Enumerable.Range(0, 8).Select(async index =>
        {
            var input = Path.Combine(_root, $"concurrent-{index}.srt");
            var output = Path.Combine(_root, $"concurrent-{index}.ass");
            await File.WriteAllTextAsync(
                input,
                $"1\n00:00:01,000 --> 00:00:03,000\nUnique subtitle {index}\n");
            await converter.ConvertAsync(
                input,
                output,
                SubtitleOutputFormat.AdvancedSubStationAlpha,
                playResX: 1920,
                playResY: 1080);
            var text = await File.ReadAllTextAsync(output);
            Assert.Contains($"Unique subtitle {index}", text);
            Assert.DoesNotContain($"Unique subtitle {(index + 1) % 8}", text);
        });

        await Task.WhenAll(tasks);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
