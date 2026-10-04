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
            "1\n00:00:00,000 --> 00:00:01,000\n<font color = FC8046 face = 휴먼편지체>테스트</font>\n",
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
            "464\r\n00:17:57,970 --> 00:17:59,544\r\n"
            + "<font color = #BBDDF7><font face = Test Family>첫째</font></font>\r\n"
            + "<font color = #B4EDE2>둘째 줄</font></font>\r\n"
            + "<font color = ff00ff>　</font>\r\n",
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
            + "<font face = a시골b>"
            + "<font color = FFA29B>변</font>"
            + "<font color = F9F177>하</font>"
            + "<font color = F3C2FC>고</font> "
            + "<font color = 96E4F7>마</font>"
            + "<font color = DEFF9E>는</font>"
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
            Regex.Matches(assText, @"\\fna시골b", RegexOptions.IgnoreCase).Count >= 5,
            assText);
        Assert.Single(
            Regex.Matches(optimizedAss, @"\\fna시골b", RegexOptions.IgnoreCase).Cast<Match>());
        Assert.DoesNotContain(@"{\fn\c", optimizedAss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsPrematureFontResetInsideInheritedColourRuns()
    {
        const string srt = "1\n00:00:00,000 --> 00:00:01,000\n"
                           + "<font face=\"a시골b\" color=\"#FFA29B\">변</font>"
                           + "<font face=\"a시골b\" color=\"#F9F177\">하</font>\n";
        const string ass = "[Script Info]\n"
                           + "[V4+ Styles]\n"
                           + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
                           + "Style: Default,SubMux Sans,75,&H00FFFFFF,&HFF00FFFF,&H00000000,&H02000000,0,0,0,0,100,100,0,0,1,4,0,2,0,0,100,1\n"
                           + "[Events]\n"
                           + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                           + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,"
                           + "{\\fna시골b\\c&H9ba2ff&}변{\\fn\\c&H77f1f9&}하\n";

        var error = Assert.Throws<InvalidDataException>(() =>
            SubtitleConversionValidator.ValidateSrtToAss(srt, ass));

        Assert.Contains("a시골b", error.Message);
    }

    [Fact]
    public void RejectsMissingFontFormattingEvenWhenTextAndTimingMatch()
    {
        const string srt = "1\n00:00:00,000 --> 00:00:01,000\n<font face=\"휴먼편지체\">테스트</font>\n";
        const string ass = "[Events]\n"
                           + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                           + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,테스트\n";

        var error = Assert.Throws<InvalidDataException>(() =>
            SubtitleConversionValidator.ValidateSrtToAss(srt, ass));

        Assert.Contains("휴먼편지체", error.Message);
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
    public async Task ReportsUnrecognizedFontColourWithoutBlockingConversion()
    {
        var source = Path.Combine(_root, "invalid-colour.srt");
        var prepared = Path.Combine(_root, "invalid-colour-prepared.srt");
        await File.WriteAllTextAsync(
            source,
            "554\n00:23:15,180 --> 00:23:16,900\n<font color=9FFDDEF>첫째</font>\n\n"
            + "555\n00:23:18,380 --> 00:23:21,260\n<font color=9FFDDEF>둘째</font>\n\n"
            + "556\n00:23:23,036 --> 00:23:24,893\n<font color=9FFDDEF>셋째</font>\n",
            new UTF8Encoding(false));

        var result = await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, prepared);

        var colour = Assert.Single(result.UnrecognizedColours);
        Assert.Equal("9FFDDEF", colour.Value);
        Assert.Equal(3, colour.Count);
        Assert.Equal(554, colour.FirstCueNumber);
        Assert.Equal("00:23:15.180", colour.FirstStart);
        Assert.Contains("<font color=\"9FFDDEF\">첫째</font>", await File.ReadAllTextAsync(prepared));

        var warning = Assert.Single(
            SubtitleCompatibilityNormalizer.CreateUnrecognizedFontColourWarnings(result));
        Assert.Contains("9FFDDEF", warning);
        Assert.Contains("3", warning);
        Assert.Contains("554", warning);
        Assert.Contains("00:23:15.180", warning);
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
