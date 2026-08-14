using SubMuxBatch.Core.Dependencies;

namespace SubMuxBatch.Core.Tests;

public sealed class DependencyLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SubMuxBatch-DependencyLocatorTests-{Guid.NewGuid():N}");

    [Fact]
    public void MissingConfiguredPathsFallBackToBundledTools()
    {
        var mkvMerge = CreateTool("tools", "mkvtoolnix", "mkvmerge.exe");
        var seConv = CreateTool("tools", "seconv", "seconv.exe");
        var locator = new DependencyLocator(_root);

        var report = locator.Locate(
            Path.Combine(_root, "deleted", "mkvmerge.exe"),
            Path.Combine(_root, "deleted", "seconv.exe"));

        Assert.Equal(mkvMerge, report.MkvMerge.Path, ignoreCase: true);
        Assert.Equal(seConv, report.SeConv.Path, ignoreCase: true);
        Assert.True(report.IsReady);
    }

    [Fact]
    public void ExistingConfiguredPathsArePreferredOverAutomaticCandidates()
    {
        var configuredMkvMerge = CreateTool("configured", "mkvmerge.exe");
        var configuredSeConv = CreateTool("configured", "seconv.exe");
        CreateTool("tools", "mkvtoolnix", "mkvmerge.exe");
        CreateTool("tools", "seconv", "seconv.exe");
        var locator = new DependencyLocator(_root);

        var report = locator.Locate(configuredMkvMerge, configuredSeConv);

        Assert.Equal(configuredMkvMerge, report.MkvMerge.Path, ignoreCase: true);
        Assert.Equal(configuredSeConv, report.SeConv.Path, ignoreCase: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateTool(params string[] segments)
    {
        var path = segments.Aggregate(_root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return Path.GetFullPath(path);
    }
}
