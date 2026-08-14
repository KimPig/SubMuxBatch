using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Tests;

public sealed class ConcurrentJobSettingsTests
{
    [Fact]
    public void DefaultsAndLegacySettingsUseOneWorker()
    {
        Assert.Equal(1, new AppSettings().ConcurrentJobCount);
        Assert.Equal(1, AppSettings.Deserialize("{}").ConcurrentJobCount);
    }

    [Fact]
    public void CopyPreservesConcurrentJobCount()
    {
        var copy = new AppSettings { ConcurrentJobCount = 4 }.Copy();

        Assert.Equal(4, copy.ConcurrentJobCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void ValidatesBoundaryValues(int value)
    {
        new AppSettings { ConcurrentJobCount = value }.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void RejectsValuesOutsideSupportedRange(int value)
    {
        var settings = new AppSettings { ConcurrentJobCount = value };

        var exception = Assert.Throws<InvalidOperationException>(settings.Validate);
        Assert.Contains("1~8", exception.Message);
    }
}
