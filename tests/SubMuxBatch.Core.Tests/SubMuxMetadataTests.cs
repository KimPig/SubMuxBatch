using System.Xml.Linq;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class SubMuxMetadataTests
{
    [Fact]
    public void GlobalTagsContainDedicatedMarkerAndComment()
    {
        var document = XDocument.Parse(SubMuxMetadata.CreateGlobalTagsXml("v2026.08.17+commit"));
        var tags = document.Descendants("Simple")
            .ToDictionary(
                element => element.Element("Name")?.Value ?? string.Empty,
                element => element.Element("String")?.Value ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Equal("2026.08.17", tags[SubMuxMetadata.VersionTagName]);
        Assert.Equal(SubMuxMetadata.CommentValue, tags[SubMuxMetadata.CommentTagName]);
    }

    [Theory]
    [InlineData("Processed", null)]
    [InlineData(null, "Processed by SubMux Batch")]
    [InlineData(null, "Source note / Processed by SubMux Batch")]
    public void RecognizesDedicatedOrCommentMarker(string? dedicatedTag, string? comment)
    {
        Assert.True(SubMuxMetadata.IsProcessed(dedicatedTag, comment));
    }

    [Fact]
    public void IgnoresUnrelatedMetadata()
    {
        Assert.False(SubMuxMetadata.IsProcessed(null, "Unrelated comment"));
    }
}
