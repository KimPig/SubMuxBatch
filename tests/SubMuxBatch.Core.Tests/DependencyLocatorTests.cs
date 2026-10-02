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
        var locator = new DependencyLocator(_root, Path.Combine(_root, "missing-bundled", "mkvmerge.exe"));

        var report = locator.Locate(Path.Combine(_root, "deleted", "mkvmerge.exe"));

        Assert.Equal(mkvMerge, report.MkvMerge.Path, ignoreCase: true);
        Assert.True(report.IsReady);
    }

    [Fact]
    public void ExistingConfiguredPathsArePreferredOverAutomaticCandidates()
    {
        var configuredMkvMerge = CreateTool("configured", "mkvmerge.exe");
        CreateTool("tools", "mkvtoolnix", "mkvmerge.exe");
        var locator = new DependencyLocator(_root, Path.Combine(_root, "missing-bundled", "mkvmerge.exe"));

        var report = locator.Locate(configuredMkvMerge);

        Assert.Equal(configuredMkvMerge, report.MkvMerge.Path, ignoreCase: true);
    }

    [Fact]
    public void BundledToolIsPreferredOverPreviouslySavedAutomaticPath()
    {
        var bundledMkvMerge = CreateTool("bundled", "102.0", "mkvmerge.exe");
        var automaticMkvMerge = CreateTool("tools", "mkvtoolnix", "mkvmerge.exe");
        var locator = new DependencyLocator(_root, bundledMkvMerge);

        var report = locator.Locate(automaticMkvMerge);

        Assert.Equal(bundledMkvMerge, report.MkvMerge.Path, ignoreCase: true);
    }

    [Fact]
    public void ExplicitCustomPathIsPreferredOverBundledTool()
    {
        var bundledMkvMerge = CreateTool("bundled", "102.0", "mkvmerge.exe");
        var configuredMkvMerge = CreateTool("custom", "mkvmerge.exe");
        var locator = new DependencyLocator(_root, bundledMkvMerge);

        var report = locator.Locate(configuredMkvMerge);

        Assert.Equal(configuredMkvMerge, report.MkvMerge.Path, ignoreCase: true);
    }

    [Fact]
    public void ExplicitOverrideCanSelectAnOtherwiseAutomaticPath()
    {
        var bundledMkvMerge = CreateTool("bundled", "102.0", "mkvmerge.exe");
        var automaticMkvMerge = CreateTool("tools", "mkvtoolnix", "mkvmerge.exe");
        var locator = new DependencyLocator(_root, bundledMkvMerge);

        var report = locator.Locate(automaticMkvMerge, preferConfigured: true);

        Assert.Equal(automaticMkvMerge, report.MkvMerge.Path, ignoreCase: true);
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
