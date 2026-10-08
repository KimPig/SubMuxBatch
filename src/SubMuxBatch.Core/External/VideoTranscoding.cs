using System.Globalization;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.External;

public sealed record VideoTranscodeRequest(
    string SourcePath,
    int SourceVideoIndex,
    string OutputPath,
    VideoQualityProfile QualityProfile,
    X265Preset CustomPreset,
    VideoRateControlMode CustomRateControl,
    int CustomCrf,
    int CustomBitrateKbps,
    X265Tune CustomTune,
    VideoCpuUsageMode CpuUsage,
    int CustomThreadCount,
    string CustomParameters,
    long? SourceDurationNanoseconds);

public sealed record VideoTranscodeResult(IReadOnlyList<string> Warnings);

internal sealed record ResolvedVideoEncodingSettings(
    string Preset,
    VideoRateControlMode RateControl,
    int Crf,
    int BitrateKbps,
    X265Tune Tune,
    int? ThreadCount);

public interface IVideoTranscoder
{
    Task<VideoTranscodeResult> TranscodeAsync(
        VideoTranscodeRequest request,
        Action<int>? onProgress = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default);
}

public sealed record PlannedVideoTranscode(MkvTrackInfo SourceTrack, int SourceVideoIndex);

public sealed record GeneratedVideoTrack(string FilePath, MkvTrackInfo SourceTrack);

public sealed record VideoMuxPlan(GeneratedVideoTrack GeneratedTrack);

public static class VideoConversionPlanner
{
    public static PlannedVideoTranscode? Create(MkvInspection source, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var videos = source.Tracks
            .Where(static track => string.Equals(track.Type, "video", StringComparison.OrdinalIgnoreCase))
            .Select((track, index) => new PlannedVideoTranscode(track, index))
            .ToArray();
        if (videos.Length == 0)
        {
            throw new InvalidOperationException(CoreText.Get("Mkv_ValidationVideoMissing"));
        }
        if (videos.Length > 1 && settings.VideoProcessingMode != VideoProcessingMode.KeepOriginal)
        {
            throw new JobSkippedException(CoreText.Get("Video_MultipleTracksUnsupported"));
        }

        var video = videos[0];
        return settings.VideoProcessingMode switch
        {
            VideoProcessingMode.KeepOriginal => null,
            VideoProcessingMode.ConvertNonHevc when IsHevc(video.SourceTrack) => null,
            VideoProcessingMode.ConvertNonHevc or VideoProcessingMode.ReencodeAll => video,
            _ => throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoEncoding"))
        };
    }

    public static bool IsHevc(MkvTrackInfo track) =>
        track.CodecId.Contains("HEVC", StringComparison.OrdinalIgnoreCase)
        || track.CodecId.Contains("H265", StringComparison.OrdinalIgnoreCase)
        || track.CodecName?.Contains("HEVC", StringComparison.OrdinalIgnoreCase) == true
        || track.CodecName?.Contains("H.265", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class BundledFfmpegVideoTranscoder(
    IProcessRunner processRunner,
    BundledFfmpegProvider? provider = null) : IVideoTranscoder
{
    private readonly BundledFfmpegProvider _provider = provider ?? new BundledFfmpegProvider();

    public async Task<VideoTranscodeResult> TranscodeAsync(
        VideoTranscodeRequest request,
        Action<int>? onProgress = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var executable = await _provider.GetExecutablePathAsync(cancellationToken).ConfigureAwait(false);
        var settings = ResolveEncodingSettings(request);
        var x265Parameters = new List<string> { "repeat-headers=1" };
        if (!string.IsNullOrWhiteSpace(request.CustomParameters))
        {
            x265Parameters.Add(request.CustomParameters.Trim().Trim(':'));
        }
        if (settings.ThreadCount.HasValue)
        {
            x265Parameters.Add($"pools={settings.ThreadCount.Value}");
        }

        var warnings = new List<string>();
        if (settings.RateControl == VideoRateControlMode.TwoPassAverageBitrate)
        {
            var firstPassParameters = string.Join(':', x265Parameters.Append("pass=1"));
            var firstPassArguments = BuildArguments(
                request,
                settings,
                firstPassParameters,
                outputPath: "NUL",
                outputFormat: "null");
            var firstPass = await RunPassAsync(
                executable,
                firstPassArguments,
                request,
                progressStart: 0,
                progressLength: 50,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccessful(firstPass, outputPath: null);
            warnings.AddRange(ExternalToolDiagnostics.FilterSuccessfulProbeNoise(firstPass.StandardError));
        }

        var finalParameters = settings.RateControl == VideoRateControlMode.TwoPassAverageBitrate
            ? string.Join(':', x265Parameters.Append("pass=2"))
            : string.Join(':', x265Parameters);
        var arguments = BuildArguments(
            request,
            settings,
            finalParameters,
            request.OutputPath,
            outputFormat: "matroska");
        var result = await RunPassAsync(
            executable,
            arguments,
            request,
            settings.RateControl == VideoRateControlMode.TwoPassAverageBitrate ? 50 : 0,
            settings.RateControl == VideoRateControlMode.TwoPassAverageBitrate ? 50 : 100,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessful(result, request.OutputPath);
        warnings.AddRange(ExternalToolDiagnostics.FilterSuccessfulProbeNoise(result.StandardError));
        warnings = warnings.Distinct(StringComparer.Ordinal).ToList();
        foreach (var warning in warnings)
        {
            onOutput?.Invoke(warning);
        }
        return new VideoTranscodeResult(warnings);

        async Task<ProcessResult> RunPassAsync(
            string command,
            IReadOnlyList<string> passArguments,
            VideoTranscodeRequest transcodeRequest,
            int progressStart,
            int progressLength,
            CancellationToken token)
        {
            var durationMicroseconds = transcodeRequest.SourceDurationNanoseconds.HasValue
                ? transcodeRequest.SourceDurationNanoseconds.Value / 1000d
                : 0d;
            void HandleOutput(string line)
            {
                if (durationMicroseconds <= 0
                    || !line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    || !long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var current))
                {
                    return;
                }
                var passPercent = Math.Clamp(current / durationMicroseconds * 100d, 0d, 100d);
                onProgress?.Invoke(Math.Clamp(
                    progressStart + (int)Math.Round(passPercent * progressLength / 100d),
                    0,
                    100));
            }

            return await processRunner.RunAsync(
                new ProcessRequest(command, passArguments, Path.GetDirectoryName(transcodeRequest.OutputPath)),
                HandleOutput,
                token).ConfigureAwait(false);
        }

        void EnsureSuccessful(ProcessResult passResult, string? outputPath)
        {
            if (passResult.ExitCode == 0
                && (outputPath is null
                    || File.Exists(outputPath) && new FileInfo(outputPath).Length > 0))
            {
                return;
            }
            var details = string.IsNullOrWhiteSpace(passResult.StandardError)
                ? passResult.StandardOutput
                : passResult.StandardError;
            throw new InvalidOperationException(
                CoreText.Get("Ffmpeg_VideoTranscodeFailed", passResult.ExitCode, details.Trim()));
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        VideoTranscodeRequest request,
        ResolvedVideoEncodingSettings settings,
        string x265Parameters,
        string outputPath,
        string outputFormat)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "repeat+level+warning", "-nostdin", "-y", "-xerror", "-copyts",
            "-i", request.SourcePath,
            "-map", $"0:v:{request.SourceVideoIndex}",
            "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            "-vf", "format=yuv420p10le",
            "-c:v", "libx265",
            "-preset", settings.Preset
        };
        if (settings.RateControl == VideoRateControlMode.ConstantQuality)
        {
            arguments.AddRange(["-crf", settings.Crf.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            arguments.AddRange(["-b:v", $"{settings.BitrateKbps.ToString(CultureInfo.InvariantCulture)}k"]);
        }
        if (settings.Tune == X265Tune.Animation)
        {
            arguments.AddRange(["-tune", "animation"]);
        }
        arguments.AddRange([
            "-x265-params", x265Parameters,
            "-fps_mode", "passthrough",
            "-avoid_negative_ts", "disabled",
            "-f", outputFormat,
            "-progress", "pipe:1",
            "-nostats",
            outputPath
        ]);
        return arguments;
    }

    internal static ResolvedVideoEncodingSettings ResolveEncodingSettings(VideoTranscodeRequest request)
    {
        int? threads = request.CpuUsage switch
        {
            VideoCpuUsageMode.Auto => null,
            VideoCpuUsageMode.Low => Math.Max(1, Environment.ProcessorCount / 4),
            VideoCpuUsageMode.Normal => Math.Max(1, Environment.ProcessorCount / 2),
            VideoCpuUsageMode.Maximum => Environment.ProcessorCount,
            VideoCpuUsageMode.Custom => request.CustomThreadCount,
            _ => throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoEncoding"))
        };
        return request.QualityProfile switch
        {
            VideoQualityProfile.Fast => new("faster", VideoRateControlMode.ConstantQuality, 24, 0, X265Tune.None, threads),
            VideoQualityProfile.Balanced => new("medium", VideoRateControlMode.ConstantQuality, 23, 0, X265Tune.None, threads),
            VideoQualityProfile.HighQuality => new("medium", VideoRateControlMode.ConstantQuality, 21, 0, X265Tune.None, threads),
            VideoQualityProfile.Custom => new(
                ToArgument(request.CustomPreset),
                request.CustomRateControl,
                request.CustomCrf,
                request.CustomBitrateKbps,
                request.CustomTune,
                threads),
            _ => throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoEncoding"))
        };
    }

    private static string ToArgument(X265Preset preset) => preset switch
    {
        X265Preset.Faster => "faster",
        X265Preset.Fast => "fast",
        X265Preset.Medium => "medium",
        X265Preset.Slow => "slow",
        _ => throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoEncoding"))
    };
}
