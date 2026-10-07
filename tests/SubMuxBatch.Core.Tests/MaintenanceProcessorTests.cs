using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Media;
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

    [Fact]
    public void MaintenancePolicyUsesCurrentBasicModeSettings()
    {
        var settings = new AppSettings
        {
            MaintenanceUpdateAssStyle = true,
            MaintenanceUpdateFonts = true,
            MaintenanceApplyAudioSettings = true,
            MaintenanceRefreshTags = true,
            MaintenanceUpdateLapseSync = true,
            AttachAssStyleFonts = false,
            RemoveExistingFontAttachments = true,
            EnableLapseSync = false
        };

        var policy = MaintenanceProcessor.CreateApplicationPolicy(settings);

        Assert.True(policy.UpdateSubtitles);
        Assert.False(policy.AttachFonts);
        Assert.True(policy.RemoveFonts);
        Assert.True(policy.ApplyAudio);
        Assert.True(policy.RefreshTags);
        Assert.False(policy.ApplyLapse);
        Assert.False(policy.CheckLapse);

        settings.EnableLapseValidationCheck = true;
        policy = MaintenanceProcessor.CreateApplicationPolicy(settings);
        Assert.False(policy.ApplyLapse);
        Assert.True(policy.CheckLapse);

        settings.AttachAssStyleFonts = true;
        settings.EnableLapseSync = true;
        settings.EnableLapseValidationCheck = false;
        policy = MaintenanceProcessor.CreateApplicationPolicy(settings);
        Assert.True(policy.AttachFonts);
        Assert.True(policy.ApplyLapse);
        Assert.False(policy.CheckLapse);
    }

    [Fact]
    public void AllMaintenanceCategoriesAreEnabledByDefault()
    {
        var settings = new AppSettings();

        Assert.True(settings.MaintenanceUpdateAssStyle);
        Assert.True(settings.MaintenanceUpdateFonts);
        Assert.True(settings.MaintenanceApplyAudioSettings);
        Assert.True(settings.MaintenanceRefreshTags);
        Assert.True(settings.MaintenanceUpdateLapseSync);
    }

    [Fact]
    public void ChapterAndMetadataCleanupDoNotDependOnTagRefresh()
    {
        var settings = new AppSettings
        {
            MaintenanceRefreshTags = false,
            RemoveChapters = true,
            CleanOutputMetadata = true
        };

        var policy = MaintenanceProcessor.CreateApplicationPolicy(settings);

        Assert.False(policy.RefreshTags);
        Assert.True(policy.RemoveChapters);
        Assert.True(policy.CleanMetadata);
    }

    [Fact]
    public void StandardSrtSelectionRequiresSubMuxTrackIdentity()
    {
        var styled = SubtitleTrack(2, "S_TEXT/ASS", "스타일 자막 (ASS)", "kor");
        var unnamed = SubtitleTrack(3, "S_TEXT/UTF8", null, "kor");

        Assert.Null(MaintenanceProcessor.SelectStandardSrtTrack([styled, unnamed], styled));
    }

    [Fact]
    public void StandardSrtSelectionUsesNameAndLanguageToResolveDuplicates()
    {
        var styled = SubtitleTrack(2, "S_TEXT/ASS", "Styled subtitles (ASS)", "kor");
        var korean = SubtitleTrack(3, "S_TEXT/UTF8", "Standard subtitles (SRT)", "kor");
        var english = SubtitleTrack(4, "S_TEXT/UTF8", "Standard subtitles (SRT)", "eng");

        Assert.Equal(korean, MaintenanceProcessor.SelectStandardSrtTrack([styled, korean, english], styled));
    }

    [Theory]
    [InlineData(false, false, "SRT", false, null, false)]
    [InlineData(true, false, "SRT", false, null, true)]
    [InlineData(true, false, "SRT", true, "AUTO|AUTO|6|8", false)]
    [InlineData(true, false, "SRT", true, "NOSPLIT|AUTO|6|8", true)]
    [InlineData(true, false, "SRT", true, null, false)]
    [InlineData(true, false, SubMuxMetadata.LegacyAssOrUnknownSource, false, null, true)]
    [InlineData(true, true, SubMuxMetadata.LegacyAssOrUnknownSource, true, null, true)]
    public void LapseDecisionUsesBasicModeStateAndStoredProfile(
        bool apply,
        bool force,
        string source,
        bool hasMarker,
        string? storedProfile,
        bool expected)
    {
        Assert.Equal(expected, MaintenanceProcessor.ShouldRunLapse(
            apply,
            force,
            source,
            hasMarker,
            storedProfile,
            "AUTO|AUTO|6|8"));
    }

    [Fact]
    public void DisabledStoredPolicyRequiresForcedMaintenanceResync()
    {
        Assert.False(MaintenanceProcessor.ShouldRunLapse(
            true,
            false,
            "SRT",
            false,
            null,
            "AUTO|AUTO|6|8",
            "DISABLED"));
        Assert.True(MaintenanceProcessor.ShouldRunLapse(
            true,
            true,
            "SRT",
            false,
            null,
            "AUTO|AUTO|6|8",
            "DISABLED"));
    }

    [Fact]
    public void IndependentLapseResultsMustDescribeTheSameTimingChange()
    {
        var left = new LapseSyncResult(
            LapseVerdict.Solid, "auto/shifted", "audio", 250, 1, 10, 1, [], "left.ass", null);
        var same = new LapseSyncResult(
            LapseVerdict.Solid, "nosplit/shifted", "audio", 250, 1, 9, 1, [], "right.srt", null);
        var different = same with { OffsetMilliseconds = 500 };

        Assert.True(MaintenanceProcessor.LapseResultsAreCompatible(left, same));
        Assert.False(MaintenanceProcessor.LapseResultsAreCompatible(left, different));
        Assert.False(MaintenanceProcessor.LapseResultsAreCompatible(
            left,
            same with { Verdict = LapseVerdict.Unsure, OutputPath = null }));
    }

    [Theory]
    [InlineData("ASS", true, false, true)]
    [InlineData("ASS+SRT", true, true, false)]
    [InlineData("SRT", true, true, false)]
    [InlineData(SubMuxMetadata.LegacyAssOrUnknownSource, false, false, true)]
    [InlineData(SubMuxMetadata.LegacyAssOrUnknownSource, true, true, true)]
    [InlineData(SubMuxMetadata.LegacyAssOrUnknownSource, true, false, false)]
    public void AssCanonicalClassificationHonorsSourceAndLegacyTextComparison(
        string source,
        bool pairedSrtExists,
        bool textMatches,
        bool expected)
    {
        Assert.Equal(expected, MaintenanceProcessor.IsAssCanonicalSource(
            source,
            pairedSrtExists,
            textMatches));
    }

    [Fact]
    public void OutputValidationRejectsSubtitleMetadataChangesEvenWhenCountsMatch()
    {
        var sourceAss = SubtitleTrack(2, "S_TEXT/ASS", "스타일 자막 (ASS)", "kor") with
        {
            DefaultTrack = true
        };
        var sourceSrt = SubtitleTrack(3, "S_TEXT/UTF8", "일반 자막 (SRT)", "kor");
        var source = new MkvInspection(
        [
            new MkvTrackInfo("video", "V_MPEGH/ISO/HEVC", true, false, null, null, null, 0),
            sourceAss,
            sourceSrt
        ], [], 0, DurationNanoseconds: 1_000_000_000);
        var output = source with
        {
            Tracks =
            [
                source.Tracks[0],
                sourceAss with { Id = 4 },
                sourceSrt with { Id = 5, TrackName = "Wrong name" }
            ]
        };
        var replacements = new[]
        {
            new SubtitleTrackReplacement("replacement.ass", sourceAss),
            new SubtitleTrackReplacement("replacement.srt", sourceSrt)
        };

        Assert.Throws<InvalidOperationException>(() => MaintenanceProcessor.ValidateOutput(
            source,
            output,
            replacements,
            null,
            null,
            false,
            false,
            false,
            []));
    }

    [Theory]
    [InlineData("ASS+SRT", true)]
    [InlineData("ASS+SMI", true)]
    [InlineData("ASS", false)]
    [InlineData(SubMuxMetadata.LegacyAssOrUnknownSource, false)]
    public void OriginalAssPairSourcesRequireTheirStandardSubtitle(string source, bool expected)
    {
        Assert.Equal(expected, MaintenanceProcessor.SourceRequiresPairedStandardSrt(source));
    }

    [Fact]
    public void TagUpdateRemovesOnlySubMuxMarkersWhenTaggingIsDisabled()
    {
        var source = """
            <Tags><Tag><Targets />
              <Simple><Name>TITLE</Name><String>Keep me</String></Simple>
              <Simple><Name>SUBMUX_BATCH_VERSION</Name><String>old</String></Simple>
              <Simple><Name>SUBMUX_BATCH_PROCESSED</Name><String>Processed by SubMux Batch</String></Simple>
              <Simple><Name>COMMENT</Name><String>Processed by SubMux Batch</String></Simple>
            </Tag></Tags>
            """;

        var updated = MaintenanceProcessor.UpdateTags(source, addSubMuxTags: false);

        Assert.Contains("Keep me", updated);
        Assert.DoesNotContain(SubMuxMetadata.VersionTagName, updated);
        Assert.DoesNotContain(SubMuxMetadata.ProcessedTagName, updated);
        Assert.DoesNotContain(SubMuxMetadata.ProcessedValue, updated);
    }

    private static MkvTrackInfo SubtitleTrack(int id, string codec, string? name, string language) =>
        new("subtitles", codec, false, false, language, language, name, id);
}
