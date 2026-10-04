using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class AudioTranscoderWarningTests
{
    [Fact]
    public void BundledFfmpegInstallDirectoryUsesTheFfmpegVersion()
    {
        Assert.Equal(
            Path.Combine(AppSettings.SettingsDirectory, "tools", "ffmpeg", BundledFfmpegProvider.Version),
            BundledFfmpegProvider.InstallDirectory);
        Assert.EndsWith(
            Path.Combine("ffmpeg", "8.1"),
            BundledFfmpegProvider.InstallDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FiltersCodecParameterProbeWarningsAndTheirAdvice()
    {
        const string stderr = """
                              [in#0/matroska,webm @ 0001] [warning] Could not find codec parameters for stream 4 (Attachment: none): unknown codec
                              Consider increasing the value for the 'analyzeduration' (0) and 'probesize' (5000000) options
                              [matroska,webm @ 0002] Could not find codec parameters for stream 3 (Subtitle: hdmv_pgs_subtitle): unspecified size
                              Consider increasing the value for the 'analyzeduration' (0) and 'probesize' (5000000) options
                              [mjpeg @ 0003] Could not find codec parameters for stream 2 (Video: mjpeg): unspecified size
                              """;

        Assert.Empty(BundledFfmpegAudioTranscoder.FilterWarnings(stderr));
    }

    [Fact]
    public void PreservesRealMediaWarningsAndUnrelatedProbeAdvice()
    {
        const string decodeWarning = "[warning] damaged audio packet at timestamp 12.4";
        const string probeAdvice = "Consider increasing the value for the 'analyzeduration' (0) and 'probesize' (5000000) options";

        var warnings = BundledFfmpegAudioTranscoder.FilterWarnings($"{decodeWarning}\n{probeAdvice}\n");

        Assert.Equal([decodeWarning, probeAdvice], warnings);
    }
}
