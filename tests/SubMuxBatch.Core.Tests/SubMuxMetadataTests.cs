using System.Xml.Linq;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class SubMuxMetadataTests
{
    [Fact]
    public void GlobalTagsContainOnlyDedicatedVersionAndProcessedMarker()
    {
        var document = XDocument.Parse(SubMuxMetadata.CreateGlobalTagsXml("v2026.08.17+commit"));
        var tags = document.Descendants("Simple")
            .ToDictionary(
                element => element.Element("Name")?.Value ?? string.Empty,
                element => element.Element("String")?.Value ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Equal("2026.08.17", tags[SubMuxMetadata.VersionTagName]);
        Assert.Equal(SubMuxMetadata.ProcessedValue, tags[SubMuxMetadata.ProcessedTagName]);
        Assert.Equal(2, tags.Count);
        Assert.DoesNotContain(SubMuxMetadata.LegacyCommentTagName, tags.Keys);
    }

    [Theory]
    [InlineData("2026.08.18", null, null)]
    [InlineData(null, "Processed by SubMux Batch", null)]
    [InlineData(null, null, "Processed by SubMux Batch")]
    [InlineData(null, null, "Source note / Processed by SubMux Batch")]
    public void RecognizesCurrentAndLegacyMarkers(
        string? version,
        string? processedMarker,
        string? legacyComment)
    {
        Assert.True(SubMuxMetadata.IsProcessed(version, processedMarker, legacyComment));
    }

    [Fact]
    public void IgnoresUnrelatedMetadata()
    {
        Assert.False(SubMuxMetadata.IsProcessed(null, null, "Unrelated comment"));
    }

    [Theory]
    [InlineData("ASS")]
    [InlineData("SRT")]
    [InlineData("SMI")]
    [InlineData("ASS+SRT")]
    [InlineData("ASS+SMI")]
    [InlineData(SubMuxMetadata.LegacySrtOrSmiSource)]
    [InlineData(SubMuxMetadata.LegacyAssOrUnknownSource)]
    public void AddsAndReadsSubtitleSourceMarker(string source)
    {
        const string ass = "[Script Info]\nScriptType: v4.00+\n[V4+ Styles]\n";

        var marked = SubMuxMetadata.AddOrReplaceSubtitleSourceMarker(ass, source);

        Assert.Contains($"; SUBMUX_SUBTITLE_SOURCE={source}", marked, StringComparison.Ordinal);
        Assert.Equal(source, SubMuxMetadata.ReadSubtitleSourceMarker(marked));
    }

    [Fact]
    public void SubtitleSourceMarkerIsReadOnlyFromScriptInfo()
    {
        const string ass = """
                           [Script Info]
                           ScriptType: v4.00+
                           [Events]
                           Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                           Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,; SUBMUX_SUBTITLE_SOURCE=SRT
                           ; SUBMUX_SUBTITLE_SOURCE=SMI
                           """;

        Assert.Null(SubMuxMetadata.ReadSubtitleSourceMarker(ass));
        Assert.False(SubMuxMetadata.HasSubtitleSourceMarker(ass));
    }

    [Fact]
    public void ConflictingSubtitleSourceMarkersAreRejected()
    {
        const string ass = """
                           [Script Info]
                           ; SUBMUX_SUBTITLE_SOURCE=SRT
                           ; SUBMUX_SUBTITLE_SOURCE=ASS
                           ScriptType: v4.00+
                           [Events]
                           """;

        Assert.Null(SubMuxMetadata.ReadSubtitleSourceMarker(ass));
        Assert.True(SubMuxMetadata.HasSubtitleSourceMarker(ass));
    }

    [Fact]
    public void LapseMarkerRoundTripsProfileInsideScriptInfo()
    {
        const string ass = "[Script Info]\nScriptType: v4.00+\n[Events]\n";

        var marked = SubMuxMetadata.AddOrReplaceAssLapseMarker(
            ass,
            "auto/shifted",
            "solid",
            "abc",
            applicationVersion: "2026.10.04",
            lapseVersion: "2.2.4",
            profile: "AUTO|AUDIOONLY|6",
            sourceFormat: "SRT");

        Assert.True(SubMuxMetadata.HasAssLapseMarker(marked));
        Assert.Equal("AUTO|AUDIOONLY|6", SubMuxMetadata.ReadAssLapseProfile(marked));
        Assert.Contains("; SUBMUX_LAPSE_SOURCE_FORMAT=SRT", marked);
    }

    [Fact]
    public void LapseMarkerTextOutsideScriptInfoIsIgnored()
    {
        const string ass = """
                           [Script Info]
                           ScriptType: v4.00+
                           [Events]
                           Comment: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,; SUBMUX_LAPSE_SYNC=2026.10.04
                           """;

        Assert.False(SubMuxMetadata.HasAssLapseMarker(ass));
    }

    [Fact]
    public void CopiesLapseMarkersWithoutCopyingDialogueTextThatLooksLikeAMarker()
    {
        const string source = """
                              [Script Info]
                              ; SUBMUX_LAPSE_SYNC=2026.10.04
                              ; SUBMUX_LAPSE_PROFILE=AUTO|AUDIOONLY|6
                              ; SUBMUX_LAPSE_REFERENCE=AUDIO
                              ; SUBMUX_LAPSE_OFFSET_MS=-250
                              ; SUBMUX_LAPSE_CONFIDENCE=0.75
                              ; SUBMUX_LAPSE_SRT_SYNC=2026.10.04
                              ; SUBMUX_LAPSE_SRT_MODE=auto/shifted
                              ; SUBMUX_LAPSE_SRT_SOURCE_FORMAT=SMI
                              ScriptType: v4.00+
                              [Events]
                              Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,; SUBMUX_LAPSE_RESULT=fake
                              """;
        const string target = "[Script Info]\nScriptType: v4.00+\n[Events]\n";

        var copied = SubMuxMetadata.CopyAssLapseMarkers(source, target);

        Assert.Contains("; SUBMUX_LAPSE_SYNC=2026.10.04", copied);
        Assert.Contains("; SUBMUX_LAPSE_PROFILE=AUTO|AUDIOONLY|6", copied);
        Assert.Contains("; SUBMUX_LAPSE_REFERENCE=AUDIO", copied);
        Assert.Contains("; SUBMUX_LAPSE_OFFSET_MS=-250", copied);
        Assert.Contains("; SUBMUX_LAPSE_CONFIDENCE=0.75", copied);
        Assert.Contains("; SUBMUX_LAPSE_SRT_SYNC=2026.10.04", copied);
        Assert.Contains("; SUBMUX_LAPSE_SRT_MODE=auto/shifted", copied);
        Assert.Contains("; SUBMUX_LAPSE_SRT_SOURCE_FORMAT=SMI", copied);
        Assert.DoesNotContain("SUBMUX_LAPSE_RESULT=fake", copied);
    }

}
