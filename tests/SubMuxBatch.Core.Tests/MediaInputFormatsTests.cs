using SubMuxBatch.Core.Domain;

namespace SubMuxBatch.Core.Tests;

public sealed class MediaInputFormatsTests
{
    [Fact]
    public void SupportedVideoExtensionsMatchProductContract()
    {
        var expected = new[]
        {
            ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".ts", ".mts", ".m2ts", ".webm"
        };

        Assert.Equal(expected, MediaInputFormats.VideoExtensions);
        Assert.All(expected, extension =>
        {
            Assert.True(MediaInputFormats.IsVideo("Movie" + extension));
            Assert.Contains("*" + extension, MediaInputFormats.VideoDialogPattern, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("*" + extension, MediaInputFormats.SupportedDialogPattern, StringComparison.OrdinalIgnoreCase);
        });
        Assert.True(MediaInputFormats.IsVideo("Movie.MP4"));
        Assert.False(MediaInputFormats.IsSupported("Movie.wmv"));
    }

    [Theory]
    [InlineData("Movie.mkv", "result_Movie.mkv")]
    [InlineData("Movie.mp4", "result_Movie.mkv")]
    [InlineData("Movie.m2ts", "result_Movie.mkv")]
    public void OutputNameAlwaysUsesMkvExtension(string input, string expected)
    {
        Assert.Equal(expected, OutputFileNaming.Create(input, "result_"));
    }
}
