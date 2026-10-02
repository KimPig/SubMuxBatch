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
    private readonly string _applicationDirectory;
    private readonly string _bundledMkvMergePath;

    public DependencyLocator(string? applicationDirectory = null, string? bundledMkvMergePath = null)
    {
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        _bundledMkvMergePath = bundledMkvMergePath ?? BundledMkvToolNixProvider.MkvMergePath;
    }

    public DependencyReport Locate(string? configuredMkvMerge, bool preferConfigured = false)
    {
        var automaticCandidates = new[]
        {
            _bundledMkvMergePath,
            Path.Combine(_applicationDirectory, "tools", "mkvtoolnix", "mkvmerge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix", "mkvmerge.exe")
        };
        var mkvMerge = LocateTool(
            "MKVToolNix",
            "mkvmerge.exe",
            configuredMkvMerge,
            automaticCandidates,
            preferConfigured);

        return new DependencyReport(mkvMerge);
    }

    private static ToolDependency LocateTool(
        string displayName,
        string executableName,
        string? configuredPath,
        IReadOnlyList<string> candidates,
        bool preferConfigured)
    {
        var paths = new List<string>();
        var normalizedAutomaticCandidates = candidates
            .Select(TryNormalize)
            .Where(static path => path is not null)
            .Cast<string>()
            .ToArray();
        var normalizedConfiguredPath = string.IsNullOrWhiteSpace(configuredPath)
            ? null
            : TryNormalize(configuredPath);
        var configuredPathIsAutomatic = !preferConfigured
                                        && normalizedConfiguredPath is not null
                                        && normalizedAutomaticCandidates.Contains(
                                            normalizedConfiguredPath,
                                            StringComparer.OrdinalIgnoreCase);
        if (normalizedConfiguredPath is not null && !configuredPathIsAutomatic)
        {
            paths.Add(normalizedConfiguredPath);
        }

        paths.AddRange(candidates);
        if (normalizedConfiguredPath is not null && configuredPathIsAutomatic)
        {
            paths.Add(normalizedConfiguredPath);
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            paths.AddRange(pathValue
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Combine(directory, executableName)));
        }

        var resolved = paths
            .Select(TryNormalize)
            .FirstOrDefault(static path => path is not null && File.Exists(path));

        if (resolved is null)
        {
            return new ToolDependency(displayName, executableName, null, null);
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

        return new ToolDependency(displayName, executableName, resolved, version);
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
