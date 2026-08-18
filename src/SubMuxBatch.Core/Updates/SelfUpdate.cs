using System.Diagnostics;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Updates;

public sealed record SelfUpdateCommand(
    int WaitProcessId,
    long WaitProcessStartTimeUtcTicks,
    string PackageDirectory,
    string TargetExecutablePath,
    string UpdateRoot)
{
    private const string ApplySwitch = "--apply-update";

    public static bool IsApplyMode(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => argument.Equals(ApplySwitch, StringComparison.OrdinalIgnoreCase));

    public static bool TryParse(IReadOnlyList<string> arguments, out SelfUpdateCommand? command)
    {
        command = null;
        if (!IsApplyMode(arguments)
            || !TryRead(arguments, "--wait-pid", out var processIdText)
            || !int.TryParse(processIdText, out var processId)
            || processId <= 0
            || !TryRead(arguments, "--wait-start-utc-ticks", out var startTicksText)
            || !long.TryParse(startTicksText, out var startTicks)
            || startTicks <= 0
            || !TryRead(arguments, "--package-directory", out var packageDirectory)
            || !TryRead(arguments, "--target-executable", out var targetExecutable)
            || !TryRead(arguments, "--update-root", out var updateRoot))
        {
            return false;
        }

        try
        {
            command = new SelfUpdateCommand(
                processId,
                startTicks,
                Path.GetFullPath(packageDirectory),
                Path.GetFullPath(targetExecutable),
                Path.GetFullPath(updateRoot));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            command = null;
            return false;
        }
    }

    private static bool TryRead(
        IReadOnlyList<string> arguments,
        string key,
        out string value)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = arguments[index + 1];
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = string.Empty;
        return false;
    }
}

public static class SelfUpdateCoordinator
{
    public static void LaunchUpdater(PreparedUpdate preparedUpdate, string targetExecutablePath)
    {
        var targetPath = Path.GetFullPath(targetExecutablePath);
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("The running application executable could not be found.", targetPath);
        }

        var process = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo(preparedUpdate.PackageExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = preparedUpdate.PackageDirectory
        };
        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(process.Id.ToString());
        startInfo.ArgumentList.Add("--wait-start-utc-ticks");
        startInfo.ArgumentList.Add(process.StartTime.ToUniversalTime().Ticks.ToString());
        startInfo.ArgumentList.Add("--package-directory");
        startInfo.ArgumentList.Add(preparedUpdate.PackageDirectory);
        startInfo.ArgumentList.Add("--target-executable");
        startInfo.ArgumentList.Add(targetPath);
        startInfo.ArgumentList.Add("--update-root");
        startInfo.ArgumentList.Add(preparedUpdate.UpdateRoot);

        if (!CanWriteDirectory(Path.GetDirectoryName(targetPath)
                               ?? throw new InvalidOperationException("The application directory is invalid.")))
        {
            startInfo.Verb = "runas";
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The update process could not be started.");
    }

    internal static bool CanWriteDirectory(string directory)
    {
        try
        {
            var testPath = Path.Combine(directory, $".submux-update-write-{Guid.NewGuid():N}.tmp");
            using (new FileStream(testPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(testPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public static class SelfUpdateApplier
{
    public static async Task ApplyAsync(
        SelfUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandPaths(command);
        await WaitForTargetProcessAsync(command, cancellationToken).ConfigureAwait(false);
        InstallPackageFiles(command.PackageDirectory, command.TargetExecutablePath);
        StartUpdatedApplication(command.TargetExecutablePath, command.UpdateRoot);
    }

    internal static void InstallPackageFiles(string packageDirectory, string targetExecutablePath)
    {
        var packageRoot = Path.GetFullPath(packageDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        var packageExecutable = Path.Combine(packageDirectory, "SubMuxBatch.exe");
        if (!File.Exists(packageExecutable))
        {
            throw new InvalidDataException("The update package does not contain SubMuxBatch.exe.");
        }

        var targetExecutable = Path.GetFullPath(targetExecutablePath);
        var targetDirectory = Path.GetDirectoryName(targetExecutable)
                              ?? throw new InvalidOperationException("The target application directory is invalid.");
        var files = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Select(source => new
            {
                Source = Path.GetFullPath(source),
                Relative = Path.GetRelativePath(packageDirectory, source)
            })
            .OrderBy(item => item.Relative.Equals("SubMuxBatch.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var file in files)
        {
            if (!file.Source.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update package contains an unsafe path.");
            }

            var destination = file.Relative.Equals("SubMuxBatch.exe", StringComparison.OrdinalIgnoreCase)
                ? targetExecutable
                : Path.GetFullPath(Path.Combine(targetDirectory, file.Relative));
            var targetRoot = targetDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
            if (!destination.Equals(targetExecutable, StringComparison.OrdinalIgnoreCase)
                && !destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update package destination is unsafe.");
            }

            ReplaceFile(file.Source, destination);
        }
    }

    private static async Task WaitForTargetProcessAsync(
        SelfUpdateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(command.WaitProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != command.WaitProcessStartTimeUtcTicks)
            {
                return;
            }

            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(2), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The original application has already exited.
        }
    }

    private static void ReplaceFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
                                  ?? throw new InvalidOperationException("The update destination is invalid."));
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.submux-update-{Guid.NewGuid():N}");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, destination, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 19
                                                   && exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(250);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void StartUpdatedApplication(string targetExecutablePath, string updateRoot)
    {
        var startInfo = new ProcessStartInfo(targetExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(targetExecutablePath)
                               ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--cleanup-update-root");
        startInfo.ArgumentList.Add(updateRoot);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The updated application could not be started.");
    }

    private static void ValidateCommandPaths(SelfUpdateCommand command)
    {
        if (!UpdateStorage.IsManagedUpdateRoot(command.UpdateRoot))
        {
            throw new InvalidOperationException("The update working directory is invalid.");
        }

        var packageRoot = Path.GetFullPath(command.PackageDirectory);
        var updateRoot = Path.GetFullPath(command.UpdateRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        if (!packageRoot.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update package directory is invalid.");
        }
    }
}

public static class UpdateStorage
{
    public static string UpdatesDirectory => Path.Combine(AppSettings.SettingsDirectory, "updates");

    public static bool TryGetCleanupRoot(IReadOnlyList<string> arguments, out string updateRoot)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--cleanup-update-root", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    updateRoot = Path.GetFullPath(arguments[index + 1]);
                    return IsManagedUpdateRoot(updateRoot);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    break;
                }
            }
        }

        updateRoot = string.Empty;
        return false;
    }

    public static bool IsManagedUpdateRoot(string path)
    {
        try
        {
            var root = Path.GetFullPath(UpdatesDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && !candidate.Equals(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static async Task CleanupAsync(string? preferredRoot = null)
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var candidates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(preferredRoot) && IsManagedUpdateRoot(preferredRoot))
        {
            candidates[Path.GetFullPath(preferredRoot)] = true;
        }

        if (Directory.Exists(UpdatesDirectory))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(UpdatesDirectory))
                {
                    candidates.TryAdd(directory, false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var (candidate, force) in candidates)
        {
            if (!IsManagedUpdateRoot(candidate))
            {
                continue;
            }

            if (!force)
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(candidate) > DateTime.UtcNow - TimeSpan.FromDays(1))
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            try
            {
                Directory.Delete(candidate, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
