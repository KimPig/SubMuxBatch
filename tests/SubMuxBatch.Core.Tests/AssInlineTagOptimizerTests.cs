using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class AssInlineTagOptimizerTests
{
    [Fact]
    public void RemovesLibSeFontResetsAndRepeatedInheritedFace()
    {
        var source = CreateAss(
            "{\\c&H112233&\\fnExample Font A}가"
            + "{\\c}{\\fn\\c&H223344&\\fnExample Font A}나"
            + "{\\c}{\\fn\\c&H334455&\\fnExample Font A}다"
            + "{\\c}{\\fnExample Font A} "
            + "{\\fn\\c&H445566&\\fnExample Font A}라"
            + "{\\c}{\\fn\\c&H556677&\\fnExample Font A}마");

        var result = AssInlineTagOptimizer.OptimizeGeneratedAss(source);

        SubtitleConversionValidator.ValidateAssOptimization(source, result);
        Assert.Contains("{\\fnExample Font A\\c&H112233&}가", result);
        Assert.Contains("{\\c&H223344&}나", result);
        Assert.Contains("{\\c&H334455&}다", result);
        Assert.Contains("{\\c} ", result);
        Assert.Contains("{\\c&H445566&}라", result);
        Assert.Contains("{\\c&H556677&}마", result);
        Assert.DoesNotContain("{\\fn\\c", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.Split("\\fnExample Font A", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void KeepsRealFontTransitionAndDropsTrailingStateOnlyBlock()
    {
        var withFollowingText = CreateAss(
            "{\\fnExample Font B\\c&H223344&}앞{\\fn\\c&H223344&\\fnExample Font A}뒤");
        var trailingOnly = CreateAss(
            "{\\fnExample Font B\\c&H223344&}앞{\\fn\\c&H223344&\\fnExample Font A}");

        var withFollowingResult = AssInlineTagOptimizer.OptimizeGeneratedAss(withFollowingText);
        var trailingResult = AssInlineTagOptimizer.OptimizeGeneratedAss(trailingOnly);

        SubtitleConversionValidator.ValidateAssOptimization(withFollowingText, withFollowingResult);
        SubtitleConversionValidator.ValidateAssOptimization(trailingOnly, trailingResult);
        Assert.Contains(
            "{\\fnExample Font B\\c&H223344&}앞{\\fnExample Font A}뒤",
            withFollowingResult);
        Assert.Contains("{\\fnExample Font B\\c&H223344&}앞", trailingResult);
        Assert.DoesNotContain("fnExample Font A", trailingResult);
    }

    [Fact]
    public void PreservesUnrelatedPositioningTags()
    {
        var source = CreateAss("{\\an8\\pos(320,72)\\fs40}위쪽");

        var result = AssInlineTagOptimizer.OptimizeGeneratedAss(source);

        SubtitleConversionValidator.ValidateAssOptimization(source, result);
        Assert.Contains(@"\an8\pos(320,72)\fs40", result);
        Assert.Contains("위쪽", result);
    }

    [Fact]
    public void LeavesTransformAndResetDialoguesUntouched()
    {
        var transform = CreateAss("{\\t(0,500,\\fs40)\\fnExample}Text");
        var reset = CreateAss("{\\rSigns\\fnExample}Text");
        var resetAfterFont = CreateAss("{\\fnExample\\rSigns}Text");

        Assert.Equal(transform, AssInlineTagOptimizer.OptimizeGeneratedAss(transform));
        Assert.Equal(reset, AssInlineTagOptimizer.OptimizeGeneratedAss(reset));
        Assert.Equal(resetAfterFont, AssInlineTagOptimizer.OptimizeGeneratedAss(resetAfterFont));
    }

    [Fact]
    public void EquivalenceValidatorRejectsVisibleTextRemoval()
    {
        var original = CreateAss("{\\fnExample}테스트");
        var damaged = CreateAss("{\\fnExample}테스");

        Assert.Throws<InvalidDataException>(() =>
            SubtitleConversionValidator.ValidateAssOptimization(original, damaged));
    }

    private static string CreateAss(string text) =>
        "[Script Info]\n"
        + "ScriptType: v4.00+\n"
        + "[V4+ Styles]\n"
        + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
        + "Style: Default,SubMux Sans,75,&H00FFFFFF,&HFF00FFFF,&H00000000,&H02000000,0,0,0,0,100,100,0,0,1,4,0,2,0,0,100,1\n"
        + "[Events]\n"
        + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
        + $"Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{text}\n";
}
