using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Dependencies;

public sealed record BundledToolStatus(
    bool IsHealthy,
    string Summary,
    string? Error,
    string MkvToolNixPath,
    string FfmpegPath,
    string LapsePath);

public sealed class BundledToolManager(
    BundledMkvToolNixProvider? mkvToolNix = null,
    BundledFfmpegProvider? ffmpeg = null,
    BundledLapseProvider? lapse = null)
{
    private readonly BundledMkvToolNixProvider _mkvToolNix = mkvToolNix ?? new BundledMkvToolNixProvider();
    private readonly BundledFfmpegProvider _ffmpeg = ffmpeg ?? new BundledFfmpegProvider();
    private readonly BundledLapseProvider _lapse = lapse ?? new BundledLapseProvider();

    public static string VersionSummary =>
        $"MKVToolNix {BundledMkvToolNixProvider.Version} · FFmpeg {BundledFfmpegProvider.Version} · libse 5.1.0 · LAPSE {BundledLapseProvider.Version}";

    public async Task<BundledToolStatus> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var mkvPath = await _mkvToolNix.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
            var ffmpegPath = await _ffmpeg.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
            var lapsePath = await _lapse.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
            var runner = new ExternalProcessRunner();
            await VerifyAsync(runner, mkvPath, ["--version"], "102.0", cancellationToken).ConfigureAwait(false);
            await VerifyAsync(runner, ffmpegPath, ["-version"], "8.1", cancellationToken).ConfigureAwait(false);
            await VerifyAsync(runner, lapsePath, ["--version"], BundledLapseProvider.Version, cancellationToken).ConfigureAwait(false);
            return new BundledToolStatus(true, VersionSummary, null, mkvPath, ffmpegPath, lapsePath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new BundledToolStatus(
                false, VersionSummary, exception.Message,
                BundledMkvToolNixProvider.MkvMergePath,
                string.Empty,
                BundledLapseProvider.ExecutablePath);
        }
    }

    private static async Task VerifyAsync(
        IProcessRunner runner,
        string executable,
        IReadOnlyList<string> arguments,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            new ProcessRequest(executable, arguments, Path.GetDirectoryName(executable)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = result.StandardOutput + Environment.NewLine + result.StandardError;
        if (result.ExitCode != 0 || !output.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Bundled tool verification failed: {Path.GetFileName(executable)} {expectedVersion}");
        }
    }
}
