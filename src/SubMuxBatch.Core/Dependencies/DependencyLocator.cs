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

public sealed record DependencyReport(ToolDependency MkvMerge, ToolDependency SeConv)
{
    public bool IsReady => MkvMerge.IsAvailable && SeConv.IsAvailable;
}

public sealed class DependencyLocator(string? applicationDirectory = null)
{
    private readonly string _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;

    public DependencyReport Locate(string? configuredMkvMerge, string? configuredSeConv)
    {
        var mkvMerge = LocateTool(
            "MKVToolNix",
            "mkvmerge.exe",
            configuredMkvMerge,
            [
                Path.Combine(_applicationDirectory, "tools", "mkvtoolnix", "mkvmerge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix", "mkvmerge.exe")
            ]);

        var seConv = LocateTool(
            "Subtitle Edit seconv",
            "seconv.exe",
            configuredSeConv,
            [
                Path.Combine(_applicationDirectory, "tools", "seconv", "seconv.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Subtitle Edit", "seconv.exe")
            ]);

        return new DependencyReport(mkvMerge, seConv);
    }

    private static ToolDependency LocateTool(
        string displayName,
        string executableName,
        string? configuredPath,
        IReadOnlyList<string> candidates)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            paths.Add(configuredPath);
        }

        paths.AddRange(candidates);

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
