using System.Security.Cryptography;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Dependencies;

public sealed class BundledLapseProvider
{
    public const string Version = "2.2.4";
    private const string ResourcePrefix = "SubMuxBatch.Core.Resources.lapse.";
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);
    private static readonly BundledFile[] Files =
    [
        new("lapse.exe", ResourcePrefix + "lapse.exe"),
        new("onnxruntime.dll", ResourcePrefix + "onnxruntime.dll"),
        new("silero_vad.onnx", ResourcePrefix + "silero_vad.onnx"),
        new("LICENSE.txt", ResourcePrefix + "LICENSE.txt"),
        new("FFmpeg-LGPL-2.1-or-later.txt", ResourcePrefix + "FFmpeg-LGPL-2.1-or-later.txt"),
        new("FFTW-GPL-2.0-or-later.txt", ResourcePrefix + "FFTW-GPL-2.0-or-later.txt"),
        new("libfvad-BSD-3-Clause.txt", ResourcePrefix + "libfvad-BSD-3-Clause.txt"),
        new("zlib-License.txt", ResourcePrefix + "zlib-License.txt"),
        new("ONNX-Runtime-MIT.txt", ResourcePrefix + "ONNX-Runtime-MIT.txt"),
        new("ONNX-Runtime-ThirdPartyNotices.txt", ResourcePrefix + "ONNX-Runtime-ThirdPartyNotices.txt"),
        new("Silero-VAD-MIT.txt", ResourcePrefix + "Silero-VAD-MIT.txt")
    ];

    private readonly string _installDirectory;
    private readonly Func<string, Stream?> _resourceLoader;

    public BundledLapseProvider()
        : this(InstallDirectory, name => typeof(BundledLapseProvider).Assembly.GetManifestResourceStream(name))
    {
    }

    internal BundledLapseProvider(string installDirectory, Func<string, Stream?> resourceLoader)
    {
        _installDirectory = Path.GetFullPath(installDirectory);
        _resourceLoader = resourceLoader;
    }

    public static string InstallDirectory => Path.Combine(
        AppSettings.SettingsDirectory, "tools", "lapse", Version);

    public static string ExecutablePath => Path.Combine(InstallDirectory, "lapse.exe");

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

            return Path.Combine(_installDirectory, "lapse.exe");
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
                                  ?? throw new InvalidOperationException($"The bundled LAPSE resource is missing: {file.ResourceName}"))
        {
            expectedHash = await SHA256.HashDataAsync(resource, cancellationToken).ConfigureAwait(false);
        }

        var destination = Path.Combine(_installDirectory, file.RelativePath);
        if (File.Exists(destination)
            && await MatchesHashAsync(destination, expectedHash, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var resource = _resourceLoader(file.ResourceName)
                                       ?? throw new InvalidOperationException($"The bundled LAPSE resource is missing: {file.ResourceName}");
            await using (var output = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await resource.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await MatchesHashAsync(temporary, expectedHash, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException($"The extracted LAPSE file failed verification: {file.RelativePath}");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<bool> MatchesHashAsync(
        string path, byte[] expectedHash, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return actualHash.AsSpan().SequenceEqual(expectedHash);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record BundledFile(string RelativePath, string ResourceName);
}
