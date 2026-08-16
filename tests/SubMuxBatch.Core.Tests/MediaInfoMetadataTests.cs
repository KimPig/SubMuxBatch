using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class MediaInfoMetadataTests
{
    [Theory]
    [InlineData("Title")]
    [InlineData("Comment")]
    [InlineData("Genre")]
    [InlineData("CUSTOM_STUDIO_TAG")]
    public void RecognizesMetadataTagNames(string name) =>
        Assert.True(MediaInfoClient.IsGeneralMetadataTagName(name));

    [Theory]
    [InlineData("Format")]
    [InlineData("Duration/String3")]
    [InlineData("FileSize")]
    [InlineData("OverallBitRate")]
    [InlineData("Encoded_Application")]
    [InlineData("SUBMUX_BATCH_VERSION")]
    public void ExcludesTechnicalAndSubMuxFields(string name) =>
        Assert.False(MediaInfoClient.IsGeneralMetadataTagName(name));
}
