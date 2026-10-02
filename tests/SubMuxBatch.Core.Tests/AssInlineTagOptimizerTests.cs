using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class AssInlineTagOptimizerTests
{
    [Fact]
    public void RemovesLibSeFontResetsAndRepeatedInheritedFace()
    {
        var source = CreateAss(
            "{\\c&H9ba2ff&\\fna시골b}변"
            + "{\\c}{\\fn\\c&H77f1f9&\\fna시골b}하"
            + "{\\c}{\\fn\\c&Hfcc2f3&\\fna시골b}고"
            + "{\\c}{\\fna시골b} "
            + "{\\fn\\c&Hf7e496&\\fna시골b}마"
            + "{\\c}{\\fn\\c&H9effde&\\fna시골b}는");

        var result = AssInlineTagOptimizer.OptimizeGeneratedAss(source);

        SubtitleConversionValidator.ValidateAssOptimization(source, result);
        Assert.Contains("{\\fna시골b\\c&H9BA2FF&}변", result);
        Assert.Contains("{\\c&H77F1F9&}하", result);
        Assert.Contains("{\\c&HFCC2F3&}고", result);
        Assert.Contains("{\\c} ", result);
        Assert.Contains("{\\c&HF7E496&}마", result);
        Assert.Contains("{\\c&H9EFFDE&}는", result);
        Assert.DoesNotContain("{\\fn\\c", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.Split("\\fna시골b", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void KeepsRealFontTransitionAndDropsTrailingStateOnlyBlock()
    {
        var withFollowingText = CreateAss(
            "{\\fn다른폰트\\c&H77f1f9&}테스트{\\fn\\c&H77f1f9&\\fna시골b}다음");
        var trailingOnly = CreateAss(
            "{\\fn다른폰트\\c&H77f1f9&}테스트{\\fn\\c&H77f1f9&\\fna시골b}");

        var withFollowingResult = AssInlineTagOptimizer.OptimizeGeneratedAss(withFollowingText);
        var trailingResult = AssInlineTagOptimizer.OptimizeGeneratedAss(trailingOnly);

        SubtitleConversionValidator.ValidateAssOptimization(withFollowingText, withFollowingResult);
        SubtitleConversionValidator.ValidateAssOptimization(trailingOnly, trailingResult);
        Assert.Contains(
            "{\\fn다른폰트\\c&H77F1F9&}테스트{\\fna시골b}다음",
            withFollowingResult);
        Assert.Contains("{\\fn다른폰트\\c&H77F1F9&}테스트", trailingResult);
        Assert.DoesNotContain("fna시골b", trailingResult);
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
