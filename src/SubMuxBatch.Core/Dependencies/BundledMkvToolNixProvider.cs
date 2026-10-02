using System.Security.Cryptography;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Dependencies;

public sealed class BundledMkvToolNixProvider
{
    public const string Version = "102.0";

    private const string ResourcePrefix = "SubMuxBatch.Core.Resources.mkvtoolnix.";
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);
    private static readonly BundledFile[] Files =
    [
        new("mkvmerge.exe", ResourcePrefix + "mkvmerge.exe"),
        new("mkvextract.exe", ResourcePrefix + "mkvextract.exe"),
        new(Path.Combine("locale", "ko", "LC_MESSAGES", "mkvtoolnix.mo"), ResourcePrefix + "locale.ko.LC_MESSAGES.mkvtoolnix.mo"),
        new("COPYING.txt", ResourcePrefix + "COPYING.txt")
    ];

    private readonly string _installDirectory;
    private readonly Func<string, Stream?> _resourceLoader;

    public BundledMkvToolNixProvider()
        : this(InstallDirectory, name => typeof(BundledMkvToolNixProvider).Assembly.GetManifestResourceStream(name))
    {
    }

    internal BundledMkvToolNixProvider(string installDirectory, Func<string, Stream?> resourceLoader)
    {
        _installDirectory = Path.GetFullPath(installDirectory);
        _resourceLoader = resourceLoader;
    }

    public static string InstallDirectory => Path.Combine(
        AppSettings.SettingsDirectory,
        "tools",
        "mkvtoolnix",
        Version);

    public static string MkvMergePath => Path.Combine(InstallDirectory, "mkvmerge.exe");

    public static bool IsBundledPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"'))),
                Path.GetFullPath(MkvMergePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        await ExtractionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_installDirectory);
            foreach (var file in Files)
            {
                await EnsureFileAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return Path.Combine(_installDirectory, "mkvmerge.exe");
        }
        finally
        {
            ExtractionGate.Release();
        }
    }

    private async Task EnsureFileAsync(BundledFile file, CancellationToken cancellationToken)
    {
        byte[] expectedHash;
        await using (var resource = _resourceLoader(file.ResourceName)
                                  ?? throw new InvalidOperationException(
                                      $"The bundled MKVToolNix resource is missing: {file.ResourceName}"))
        {
            expectedHash = await SHA256.HashDataAsync(resource, cancellationToken).ConfigureAwait(false);
        }

        var destination = Path.Combine(_installDirectory, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)
            && await FileMatchesHashAsync(destination, expectedHash, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var resource = _resourceLoader(file.ResourceName)
                                       ?? throw new InvalidOperationException(
                                           $"The bundled MKVToolNix resource is missing: {file.ResourceName}");
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await resource.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await FileMatchesHashAsync(temporary, expectedHash, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException($"The extracted MKVToolNix file failed verification: {file.RelativePath}");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<bool> FileMatchesHashAsync(
        string path,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return actualHash.AsSpan().SequenceEqual(expectedHash);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record BundledFile(string RelativePath, string ResourceName);
}
