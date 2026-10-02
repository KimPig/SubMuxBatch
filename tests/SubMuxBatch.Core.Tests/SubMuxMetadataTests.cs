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
    [InlineData(SubMuxMetadata.LegacySrtOrSmiSource)]
    [InlineData(SubMuxMetadata.LegacyAssOrUnknownSource)]
    public void AddsAndReadsAssSourceMarker(string source)
    {
        const string ass = "[Script Info]\nScriptType: v4.00+\n[V4+ Styles]\n";

        var marked = SubMuxMetadata.AddAssSourceMarker(ass, source);

        Assert.Contains($"; SUBMUX_ASS_SOURCE={source}", marked, StringComparison.Ordinal);
        Assert.Equal(source, SubMuxMetadata.ReadAssSourceMarker(marked));
    }

    [Fact]
    public void ExistingAssSourceMarkerIsPreserved()
    {
        const string ass = "[Script Info]\n; SUBMUX_ASS_SOURCE=ASS\nScriptType: v4.00+\n";

        var marked = SubMuxMetadata.AddAssSourceMarker(ass, "SMI");

        Assert.Equal(ass, marked);
        Assert.Equal("ASS", SubMuxMetadata.ReadAssSourceMarker(marked));
    }
}
