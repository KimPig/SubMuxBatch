using System.Diagnostics;

namespace SubMuxBatch.Core.Dependencies;

public sealed record ToolDependency(
    string DisplayName,
    string ExecutableName,
    string? Path,
    string? Version)
{
    public bool IsAvailable => Path is not null;
}

public sealed record DependencyReport(ToolDependency MkvMerge)
{
    public bool IsReady => MkvMerge.IsAvailable;
}

public sealed class DependencyLocator
{
    private readonly string _bundledMkvMergePath;

    public DependencyLocator(string? applicationDirectory = null, string? bundledMkvMergePath = null)
    {
        _bundledMkvMergePath = bundledMkvMergePath ?? BundledMkvToolNixProvider.MkvMergePath;
    }

    public DependencyReport Locate(string? configuredMkvMerge, bool preferConfigured = false)
    {
        var resolved = TryNormalize(_bundledMkvMergePath);

        if (resolved is null || !File.Exists(resolved))
        {
            return new DependencyReport(new ToolDependency("MKVToolNix", "mkvmerge.exe", null, null));
        }

        string? version = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(resolved);
            version = info.ProductVersion ?? info.FileVersion;
        }
        catch
        {
            // A version is informative only; an executable can still be used without it.
        }

        return new DependencyReport(new ToolDependency("MKVToolNix", "mkvmerge.exe", resolved, version));
    }

    private static string? TryNormalize(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"')));
        }
        catch
        {
            return null;
        }
    }
}
