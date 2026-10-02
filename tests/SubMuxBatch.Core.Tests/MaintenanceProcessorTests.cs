using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Processing;

namespace SubMuxBatch.Core.Tests;

public sealed class MaintenanceProcessorTests
{
    [Fact]
    public void LegacyStyleComparisonRequiresExactSingleDefaultStyle()
    {
        var fingerprint = AppSettings.LegacyMalgunGothicAssStyleLine;
        var ass = """
                  [V4+ Styles]
                  Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
                  Style: Default,맑은 고딕,75,&H00FFFFFF,&HFF00FFFF,&H00000000,&H02000000,-1,0,0,0,100,100,0,0,1,4,0,2,0,0,100,1
                  """;

        Assert.True(MaintenanceProcessor.MatchesLegacyStyle(ass, fingerprint));
    }

    [Fact]
    public void LegacyStyleComparisonRejectsEquivalentNumberSpelling()
    {
        var ass = AppSettings.LegacyMalgunGothicAssStyleLine.Replace(",75,", ",75.0,", StringComparison.Ordinal);

        Assert.False(MaintenanceProcessor.MatchesLegacyStyle(ass, AppSettings.LegacyMalgunGothicAssStyleLine));
    }

    [Fact]
    public void LegacyStyleComparisonRejectsMultipleStylesEvenWhenOneMatches()
    {
        var ass = $"""
                   [V4+ Styles]
                   {AppSettings.LegacyMalgunGothicAssStyleLine}
                   Style: Signs,Arial,40,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1
                   """;

        Assert.False(MaintenanceProcessor.MatchesLegacyStyle(ass, AppSettings.LegacyMalgunGothicAssStyleLine));
    }

    [Fact]
    public void LegacyStyleComparisonRejectsCaseDifference()
    {
        var ass = AppSettings.LegacyMalgunGothicAssStyleLine.Replace("Style:", "style:", StringComparison.Ordinal);

        Assert.False(MaintenanceProcessor.MatchesLegacyStyle(ass, AppSettings.LegacyMalgunGothicAssStyleLine));
    }

    [Fact]
    public void ReplacesOnlyDefaultStyleAndPreservesDialogues()
    {
        var ass = """
                  [V4+ Styles]
                  Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
                  Style: Default,맑은 고딕,75,&H00FFFFFF,&HFF00FFFF,&H00000000,&H02000000,-1,0,0,0,100,100,0,0,1,4,0,2,0,0,100,1
                  Style: Signs,Arial,40,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1
                  [Events]
                  Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hello
                  """;

        var result = MaintenanceProcessor.ReplaceDefaultStyle(ass, AppSettings.DefaultAssStyleLine);

        Assert.Contains(AppSettings.DefaultAssStyleLine, result);
        Assert.Contains("Style: Signs,Arial,40", result);
        Assert.Contains("Dialogue: 0,0:00:01.00", result);
    }
}
