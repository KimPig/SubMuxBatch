using System.Text;
using System.Text.RegularExpressions;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class SubtitleConversionValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SubtitleConversionValidatorTests",
        Guid.NewGuid().ToString("N"));

    public SubtitleConversionValidatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AcceptsCanonicalizedFontColourAndFaceProducedByLibSe()
    {
        var source = Path.Combine(_root, "source.srt");
        var prepared = Path.Combine(_root, "prepared.srt");
        var style = Path.Combine(_root, "style.ass");
        var ass = Path.Combine(_root, "result.ass");
        await File.WriteAllTextAsync(
            source,
            "1\n00:00:00,000 --> 00:00:01,000\n<font color = 112233 face = Example Font A>테스트</font>\n",
            new UTF8Encoding(false));
        await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, prepared);
        await File.WriteAllTextAsync(
            style,
            AssStyleTemplateWriter.CreateHeader(new AppSettings()),
            new UTF8Encoding(true));

        await new LibSeSubtitleConverter().ConvertAsync(
            prepared,
            ass,
            SubtitleOutputFormat.AdvancedSubStationAlpha,
            style,
            1920,
            1080);

        SubtitleConversionValidator.ValidateSrtToAss(
            await File.ReadAllTextAsync(prepared),
            await File.ReadAllTextAsync(ass));
    }

    [Fact]
    public async Task ConvertsNamedColoursAndPassesAnUnknownColourWithAWarning()
    {
        var source = Path.Combine(_root, "named-and-unknown.srt");
        var prepared = Path.Combine(_root, "named-and-unknown-prepared.srt");
        var ass = Path.Combine(_root, "named-and-unknown.ass");
        await File.WriteAllTextAsync(
            source,
            "1\n00:00:00,000 --> 00:00:01,000\n"
            + "<font color=gray>회색</font> <font color=pink>분홍색</font> "
            + "<font color=white>흰색</font> <font color=9FFDDEF>잘못된 값</font>\n",
            new UTF8Encoding(false));

        var result = await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, prepared);
        var unknown = Assert.Single(result.UnrecognizedColours);
        Assert.Equal("9FFDDEF", unknown.Value);

        await new LibSeSubtitleConverter().ConvertAsync(
            prepared,
            ass,
            SubtitleOutputFormat.AdvancedSubStationAlpha);

        var preparedText = await File.ReadAllTextAsync(prepared);
        var assText = await File.ReadAllTextAsync(ass);
        SubtitleConversionValidator.ValidateSrtToAss(preparedText, assText);
        Assert.Contains(@"\c&H808080&", assText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\c&Hcbc0ff&", assText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\c&Hffffff&", assText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptsARealisticCueAfterOrphanFontClosingIsRemoved()
    {
        var source = Path.Combine(_root, "source-unbalanced.srt");
        var prepared = Path.Combine(_root, "prepared-unbalanced.srt");
        var ass = Path.Combine(_root, "result-unbalanced.ass");
        await File.WriteAllTextAsync(
            source,
            "1\r\n00:00:00,000 --> 00:00:01,000\r\n"
            + "<font color = #112233><font face = Example Font A>첫째</font></font>\r\n"
            + "<font color = #223344>둘째 줄</font></font>\r\n"
            + "<font color = 334455>　</font>\r\n",
            new UTF8Encoding(false));
        await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, prepared);
        await new LibSeSubtitleConverter().ConvertAsync(
            prepared,
            ass,
            SubtitleOutputFormat.AdvancedSubStationAlpha);

        var preparedText = await File.ReadAllTextAsync(prepared);
        var assText = await File.ReadAllTextAsync(ass);
        SubtitleConversionValidator.ValidateSrtToAss(preparedText, assText);
        Assert.DoesNotContain("</font>", assText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreservesRubyReadingAsSmallerTextWithoutLiteralRubyTags()
    {
        var source = Path.Combine(_root, "ruby.srt");
        var prepared = Path.Combine(_root, "ruby-prepared.srt");
        var style = Path.Combine(_root, "ruby-style.ass");
        var ass = Path.Combine(_root, "ruby.ass");
        await File.WriteAllTextAsync(
            source,
            "1\n00:00:00,000 --> 00:00:01,000\n<ruby>테스트<rt>test</rt></ruby> 뒤\n",
            new UTF8Encoding(false));
        await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, prepared, 75);
        await File.WriteAllTextAsync(
            style,
            AssStyleTemplateWriter.CreateHeader(new AppSettings()),
            new UTF8Encoding(true));

        await new LibSeSubtitleConverter().ConvertAsync(
            prepared,
            ass,
            SubtitleOutputFormat.AdvancedSubStationAlpha,
            style,
            1920,
            1080);

        var preparedText = await File.ReadAllTextAsync(prepared);
        var assText = await File.ReadAllTextAsync(ass);
        SubtitleConversionValidator.ValidateSrtToAss(preparedText, assText);
        Assert.DoesNotContain("<ruby", assText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\fs37.5", assText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"{\fs}", assText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("테스트", assText);
        Assert.Contains("test", assText);
        Assert.Contains("뒤", assText);
    }

    [Fact]
    public async Task PreservesInheritedFontFaceAcrossNestedColourRuns()
    {
        var source = Path.Combine(_root, "nested-font.srt");
        var prepared = Path.Combine(_root, "nested-font-prepared.srt");
        var style = Path.Combine(_root, "nested-font-style.ass");
        var ass = Path.Combine(_root, "nested-font.ass");
        await File.WriteAllTextAsync(
            source,
            "1\n00:00:00,000 --> 00:00:01,000\n"
            + "<font face = Example Font A>"
            + "<font color = 112233>가</font>"
            + "<font color = 223344>나</font>"
            + "<font color = 334455>다</font> "
            + "<font color = 445566>라</font>"
            + "<font color = 556677>마</font>"
            + "</font>\n",
            new UTF8Encoding(false));
        await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, prepared, 75);
        await File.WriteAllTextAsync(
            style,
            AssStyleTemplateWriter.CreateHeader(new AppSettings()),
            new UTF8Encoding(true));

        await new LibSeSubtitleConverter().ConvertAsync(
            prepared,
            ass,
            SubtitleOutputFormat.AdvancedSubStationAlpha,
            style,
            1920,
            1080);

        var preparedText = await File.ReadAllTextAsync(prepared);
        var assText = await File.ReadAllTextAsync(ass);
        SubtitleConversionValidator.ValidateSrtToAss(preparedText, assText);
        var optimizedAss = AssInlineTagOptimizer.OptimizeGeneratedAss(assText);
        SubtitleConversionValidator.ValidateAssOptimization(assText, optimizedAss);
        SubtitleConversionValidator.ValidateSrtToAss(preparedText, optimizedAss);
        Assert.True(
            Regex.Matches(assText, @"\\fnExample Font A", RegexOptions.IgnoreCase).Count >= 5,
            assText);
        Assert.Single(
            Regex.Matches(optimizedAss, @"\\fnExample Font A", RegexOptions.IgnoreCase).Cast<Match>());
        Assert.DoesNotContain(@"{\fn\c", optimizedAss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsPrematureFontResetInsideInheritedColourRuns()
    {
        const string srt = "1\n00:00:00,000 --> 00:00:01,000\n"
                           + "<font face=\"Example Font A\" color=\"#112233\">가</font>"
                           + "<font face=\"Example Font A\" color=\"#223344\">나</font>\n";
        const string ass = "[Script Info]\n"
                           + "[V4+ Styles]\n"
                           + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
                           + "Style: Default,SubMux Sans,75,&H00FFFFFF,&HFF00FFFF,&H00000000,&H02000000,0,0,0,0,100,100,0,0,1,4,0,2,0,0,100,1\n"
                           + "[Events]\n"
                           + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                           + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,"
                           + "{\\fnExample Font A\\c&H332211&}가{\\fn\\c&H443322&}나\n";

        var error = Assert.Throws<InvalidDataException>(() =>
            SubtitleConversionValidator.ValidateSrtToAss(srt, ass));

        Assert.Contains("Example Font A", error.Message);
    }

    [Fact]
    public void RejectsMissingFontFormattingEvenWhenTextAndTimingMatch()
    {
        const string srt = "1\n00:00:00,000 --> 00:00:01,000\n<font face=\"Example Font A\">테스트</font>\n";
        const string ass = "[Events]\n"
                           + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                           + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,테스트\n";

        var error = Assert.Throws<InvalidDataException>(() =>
            SubtitleConversionValidator.ValidateSrtToAss(srt, ass));

        Assert.Contains("Example Font A", error.Message);
    }

    [Fact]
    public void RejectsLiteralFontTagLeak()
    {
        const string srt = "1\n00:00:00,000 --> 00:00:01,000\n테스트\n";
        const string ass = "[Events]\n"
                           + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                           + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,<font>테스트</font>\n";

        Assert.Throws<InvalidDataException>(() =>
            SubtitleConversionValidator.ValidateSrtToAss(srt, ass));
    }

    [Fact]
    public void AcceptsAValidMillisecondCueThatCollapsesAtAssTimestampPrecision()
    {
        const string srt = "1\n"
                           + "00:00:01,000 --> 00:00:01,003\n"
                           + "Transient\n\n"
                           + "2\n"
                           + "00:00:01,003 --> 00:00:02,000\n"
                           + "Visible\n";
        const string ass = "[Events]\n"
                           + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                           + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Visible\n";
        NegativeSubtitleTimestampAdjustment[] adjustments =
        [
            new(
                3,
                "0:00:01.00 --> 0:00:01.00",
                string.Empty,
                SubtitleTimestampAdjustmentKind.RemovedInvalidRange,
                CueNumber: 1)
        ];

        var collapsed = SubtitleConversionValidator.FindAssPrecisionCollapsedSrtCues(
            srt,
            adjustments);

        Assert.Contains(1, collapsed);
        Assert.Throws<InvalidDataException>(() =>
            SubtitleConversionValidator.ValidateSrtToAss(srt, ass));
        SubtitleConversionValidator.ValidateSrtToAss(srt, ass, collapsed);
    }

    [Fact]
    public void DoesNotClassifyALongerCueAsAnAssPrecisionCollapse()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:01,010\nText\n";
        NegativeSubtitleTimestampAdjustment[] adjustments =
        [
            new(
                3,
                "0:00:01.00 --> 0:00:01.00",
                string.Empty,
                SubtitleTimestampAdjustmentKind.RemovedInvalidRange,
                CueNumber: 1)
        ];

        Assert.Empty(SubtitleConversionValidator.FindAssPrecisionCollapsedSrtCues(
            srt,
            adjustments));
    }

    [Fact]
    public async Task ReportsUnrecognizedFontColourWithoutBlockingConversion()
    {
        var source = Path.Combine(_root, "invalid-colour.srt");
        var prepared = Path.Combine(_root, "invalid-colour-prepared.srt");
        await File.WriteAllTextAsync(
            source,
            "1\n00:00:00,000 --> 00:00:01,000\n<font color=9FFDDEF>첫째</font>\n\n"
            + "2\n00:00:01,000 --> 00:00:02,000\n<font color=9FFDDEF>둘째</font>\n\n"
            + "3\n00:00:02,000 --> 00:00:03,000\n<font color=9FFDDEF>셋째</font>\n",
            new UTF8Encoding(false));

        var result = await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, prepared);

        var colour = Assert.Single(result.UnrecognizedColours);
        Assert.Equal("9FFDDEF", colour.Value);
        Assert.Equal(3, colour.Count);
        Assert.Equal(1, colour.FirstCueNumber);
        Assert.Equal("00:00:00.000", colour.FirstStart);
        Assert.Contains("<font color=\"9FFDDEF\">첫째</font>", await File.ReadAllTextAsync(prepared));

        var warning = Assert.Single(
            SubtitleCompatibilityNormalizer.CreateUnrecognizedFontColourWarnings(result));
        Assert.Contains("9FFDDEF", warning);
        Assert.Contains("3", warning);
        Assert.Contains("1", warning);
        Assert.Contains("00:00:00.000", warning);
    }

    [Theory]
    [InlineData("gray")]
    [InlineData("pink")]
    [InlineData("white")]
    [InlineData("FFDFDF")]
    [InlineData("#FFDFDF")]
    public async Task AcceptsColoursRecognizedByLibSe(string value)
    {
        var source = Path.Combine(_root, $"recognized-{Guid.NewGuid():N}.srt");
        var prepared = Path.Combine(_root, $"recognized-{Guid.NewGuid():N}-prepared.srt");
        await File.WriteAllTextAsync(
            source,
            $"1\n00:00:00,000 --> 00:00:01,000\n<font color={value}>Test</font>\n",
            new UTF8Encoding(false));

        var result = await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, prepared);

        Assert.Empty(result.UnrecognizedColours);
        Assert.Contains($"<font color=\"{value}\">Test</font>", await File.ReadAllTextAsync(prepared));
    }

    [Fact]
    public void SkipsOnlyColourComparisonForAnUnrecognizedValue()
    {
        const string srt = "1\n00:00:00,000 --> 00:00:01,000\n<font color=\"9FFDDEF\" face=\"Arial\">Test</font>\n";
        const string ass = "[V4+ Styles]\n"
                           + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
                           + "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n"
                           + "[Events]\n"
                           + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                           + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\\c&Hffffff&}Test\n";

        SubtitleConversionValidator.ValidateSrtToAss(srt, ass);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

}
