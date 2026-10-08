using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.External;

public sealed record AudioTranscodeRequest(
    string SourcePath,
    int SourceAudioIndex,
    string OutputPath,
    int? OutputChannels,
    int BitrateKbps,
    long? SourceDurationNanoseconds,
    AudioCodec Codec = AudioCodec.AacLc);

public sealed record AudioTranscodeResult(IReadOnlyList<string> Warnings);

public interface IAudioTranscoder
{
    Task<AudioTranscodeResult> TranscodeAsync(
        AudioTranscodeRequest request,
        Action<int>? onProgress = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default);
}

public sealed record GeneratedAudioTrack(
    string FilePath,
    MkvTrackInfo SourceTrack,
    int? OutputChannels,
    int BitrateKbps,
    bool DefaultTrack,
    bool ForcedTrack,
    string? TrackName,
    AudioCodec Codec = AudioCodec.AacLc);

public sealed record AudioMuxPlan(
    IReadOnlySet<int> RetainedSourceTrackIds,
    IReadOnlyList<GeneratedAudioTrack> GeneratedTracks,
    IReadOnlyDictionary<int, bool> SourceDefaultTrackOverrides);

public sealed record PlannedAudioTranscode(
    MkvTrackInfo SourceTrack,
    int SourceAudioIndex,
    int? OutputChannels,
    int BitrateKbps,
    bool DefaultTrack,
    bool ForcedTrack,
    string? TrackName,
    AudioCodec Codec = AudioCodec.AacLc);

public sealed record AudioConversionPlan(
    IReadOnlyList<MkvTrackInfo> SelectedSourceTracks,
    IReadOnlySet<int> RetainedSourceTrackIds,
    IReadOnlyList<PlannedAudioTranscode> Transcodes,
    IReadOnlyDictionary<int, bool> SourceDefaultTrackOverrides);

public static class AudioConversionPlanner
{
    public static AudioConversionPlan Create(MkvInspection source, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var indexedAudio = source.Tracks
            .Where(IsAudio)
            .Select((track, audioIndex) => new IndexedAudioTrack(track, audioIndex))
            .ToArray();
        var selected = SelectByLanguage(indexedAudio, settings).ToArray();
        var processingMode = GetProcessingMode(settings);
        if (processingMode == AudioProcessingMode.KeepOriginal)
        {
            return new AudioConversionPlan(
                selected.Select(static item => item.Track).ToArray(),
                selected.Select(static item => RequiredId(item.Track)).ToHashSet(),
                [],
                new Dictionary<int, bool>());
        }

        var retainedIds = new HashSet<int>();
        var transcodes = new List<PlannedAudioTranscode>();
        var defaultOverrides = new Dictionary<int, bool>();

        switch (settings.AudioChannelMode)
        {
            case AudioChannelMode.PreserveChannels:
                foreach (var item in selected)
                {
                    if (!NeedsTranscode(item.Track, item.Track.AudioChannels, settings, processingMode))
                    {
                        retainedIds.Add(RequiredId(item.Track));
                    }
                    else
                    {
                        transcodes.Add(CreateTranscode(item, item.Track.AudioChannels, settings));
                    }
                }
                break;

            case AudioChannelMode.ConvertToStereo:
                foreach (var item in selected)
                {
                    var channels = item.Track.AudioChannels is > 2 ? 2 : item.Track.AudioChannels;
                    if (!NeedsTranscode(item.Track, channels, settings, processingMode))
                    {
                        retainedIds.Add(RequiredId(item.Track));
                    }
                    else
                    {
                        transcodes.Add(CreateTranscode(item, channels, settings));
                    }
                }
                break;

            case AudioChannelMode.KeepMultichannelAndAddStereo:
                foreach (var item in selected.Where(static item => item.Track.AudioChannels is <= 2 or null))
                {
                    if (!NeedsTranscode(item.Track, item.Track.AudioChannels, settings, processingMode))
                    {
                        retainedIds.Add(RequiredId(item.Track));
                    }
                    else
                    {
                        transcodes.Add(CreateTranscode(item, item.Track.AudioChannels, settings));
                    }
                }

                foreach (var group in selected
                             .Where(static item => item.Track.AudioChannels is > 2)
                             .GroupBy(static item => GetLanguageKey(item.Track), StringComparer.OrdinalIgnoreCase))
                {
                    var multichannelTracks = group.ToArray();
                    foreach (var item in multichannelTracks)
                    {
                        retainedIds.Add(RequiredId(item.Track));
                    }

                    var existingStereoTracks = selected.Where(item =>
                            item.Track.AudioChannels == 2
                            && MatchesCodec(item.Track, settings.AudioCodec)
                            && processingMode != AudioProcessingMode.ReencodeAll
                            && string.Equals(GetLanguageKey(item.Track), group.Key, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var plannedStereoIndices = transcodes
                        .Select((item, index) => (Item: item, Index: index))
                        .Where(item => item.Item.OutputChannels == 2
                                       && string.Equals(
                                           GetLanguageKey(item.Item.SourceTrack),
                                           group.Key,
                                           StringComparison.OrdinalIgnoreCase))
                        .Select(static item => item.Index)
                        .ToArray();
                    var usedExistingIds = new HashSet<int>();
                    var usedPlannedIndices = new HashSet<int>();

                    foreach (var multichannel in multichannelTracks)
                    {
                        var existingStereo = FindCounterpart(
                            multichannel,
                            existingStereoTracks.Where(item => !usedExistingIds.Contains(RequiredId(item.Track))).ToArray(),
                            multichannelTracks.Length);
                        if (existingStereo is not null)
                        {
                            usedExistingIds.Add(RequiredId(existingStereo.Track));
                            if (multichannel.Track.DefaultTrack)
                            {
                                defaultOverrides[RequiredId(multichannel.Track)] = false;
                                if (!existingStereo.Track.DefaultTrack)
                                {
                                    defaultOverrides[RequiredId(existingStereo.Track)] = true;
                                }
                            }
                            continue;
                        }

                        var availablePlannedIndices = plannedStereoIndices
                            .Where(index => !usedPlannedIndices.Contains(index))
                            .ToArray();
                        var plannedStereoIndex = FindCounterpartIndex(
                            multichannel,
                            availablePlannedIndices,
                            transcodes,
                            multichannelTracks.Length);
                        if (plannedStereoIndex >= 0)
                        {
                            usedPlannedIndices.Add(plannedStereoIndex);
                            if (multichannel.Track.DefaultTrack)
                            {
                                transcodes[plannedStereoIndex] = transcodes[plannedStereoIndex] with { DefaultTrack = true };
                                defaultOverrides[RequiredId(multichannel.Track)] = false;
                            }
                            continue;
                        }

                        if (multichannel.Track.DefaultTrack && retainedIds.Contains(RequiredId(multichannel.Track)))
                        {
                            defaultOverrides[RequiredId(multichannel.Track)] = false;
                        }
                        transcodes.Add(CreateTranscode(multichannel, 2, settings, multichannel.Track.DefaultTrack));
                    }
                }
                break;

            default:
                throw new InvalidOperationException(CoreText.Get("Settings_InvalidAudioChannelMode"));
        }

        return new AudioConversionPlan(
            selected.Select(static item => item.Track).ToArray(),
            retainedIds,
            transcodes,
            defaultOverrides);
    }

    public static AudioProcessingMode GetProcessingMode(AppSettings settings) =>
        settings.AudioProcessingMode == AudioProcessingMode.KeepOriginal && settings.ConvertAudioToAac
            ? AudioProcessingMode.ConvertWhenNeeded
            : settings.AudioProcessingMode;

    public static int GetBitrateKbps(int? channels) => channels switch
    {
        <= 1 => 96,
        2 => 192,
        3 or 4 or 5 or 6 => 384,
        >= 7 => 512,
        _ => 192
    };

    private static IEnumerable<IndexedAudioTrack> SelectByLanguage(
        IReadOnlyList<IndexedAudioTrack> audioTracks,
        AppSettings settings)
    {
        if (!settings.FilterAudioTracksByLanguage || audioTracks.Count <= 1)
        {
            return audioTracks;
        }

        var selected = audioTracks.Where(item => MatchesLanguage(item.Track, settings.SelectedAudioLanguage)).ToArray();
        if (selected.Length == 0)
        {
            throw new JobSkippedException(CoreText.Get(
                "Mkv_AudioLanguageNotFound",
                GetLanguageDisplayName(settings.SelectedAudioLanguage)));
        }
        return selected;
    }

    private static PlannedAudioTranscode CreateTranscode(
        IndexedAudioTrack item,
        int? channels,
        AppSettings settings,
        bool? isDefault = null) => new(
        item.Track,
        item.AudioIndex,
        channels,
        settings.ConvertAudioToAac && settings.AudioProcessingMode == AudioProcessingMode.KeepOriginal
            ? GetBitrateKbps(channels)
            : settings.AudioBitrateKbps,
        isDefault ?? item.Track.DefaultTrack,
        item.Track.ForcedTrack,
        item.Track.TrackName,
        settings.AudioCodec);

    private static bool IsAudio(MkvTrackInfo track) =>
        string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsTranscode(
        MkvTrackInfo track,
        int? outputChannels,
        AppSettings settings,
        AudioProcessingMode processingMode) =>
        processingMode == AudioProcessingMode.ReencodeAll
        || !MatchesCodec(track, settings.AudioCodec)
        || outputChannels.HasValue && track.AudioChannels != outputChannels;

    private static bool MatchesCodec(MkvTrackInfo track, AudioCodec codec) => codec switch
    {
        AudioCodec.AacLc => track.CodecId.Contains("AAC", StringComparison.OrdinalIgnoreCase)
                            || track.CodecName?.Contains("AAC", StringComparison.OrdinalIgnoreCase) == true,
        AudioCodec.Opus => track.CodecId.Contains("OPUS", StringComparison.OrdinalIgnoreCase)
                          || track.CodecName?.Contains("OPUS", StringComparison.OrdinalIgnoreCase) == true,
        _ => false
    };

    private static int RequiredId(MkvTrackInfo track) =>
        track.Id ?? throw new InvalidOperationException(CoreText.Get("Mkv_AudioTrackIdMissing"));

    private static string GetLanguageKey(MkvTrackInfo track)
    {
        if (!string.IsNullOrWhiteSpace(track.LanguageIetf)
            && !string.Equals(track.LanguageIetf, "und", StringComparison.OrdinalIgnoreCase))
        {
            return track.LanguageIetf.Trim().Split('-', 2)[0].ToLowerInvariant();
        }

        var legacy = string.IsNullOrWhiteSpace(track.Language)
            ? "und"
            : track.Language.Trim().ToLowerInvariant();
        return legacy switch
        {
            "eng" => "en",
            "jpn" => "ja",
            "kor" => "ko",
            _ => legacy
        };
    }

    private static IndexedAudioTrack? FindCounterpart(
        IndexedAudioTrack source,
        IReadOnlyList<IndexedAudioTrack> candidates,
        int sourceCount)
    {
        var exactName = candidates.FirstOrDefault(candidate => TrackNamesMatch(source.Track, candidate.Track));
        if (exactName is not null)
        {
            return exactName;
        }
        return sourceCount == 1 && candidates.Count == 1 ? candidates[0] : null;
    }

    private static int FindCounterpartIndex(
        IndexedAudioTrack source,
        IReadOnlyList<int> candidateIndices,
        IReadOnlyList<PlannedAudioTranscode> transcodes,
        int sourceCount)
    {
        foreach (var index in candidateIndices)
        {
            if (TrackNamesMatch(source.Track, transcodes[index].SourceTrack))
            {
                return index;
            }
        }
        return sourceCount == 1 && candidateIndices.Count == 1 ? candidateIndices[0] : -1;
    }

    private static bool TrackNamesMatch(MkvTrackInfo left, MkvTrackInfo right) =>
        !string.IsNullOrWhiteSpace(left.TrackName)
        && !string.IsNullOrWhiteSpace(right.TrackName)
        && string.Equals(left.TrackName.Trim(), right.TrackName.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesLanguage(MkvTrackInfo track, AudioTrackLanguage language)
    {
        var (legacy, ietf) = language switch
        {
            AudioTrackLanguage.English => ("eng", "en"),
            AudioTrackLanguage.Japanese => ("jpn", "ja"),
            AudioTrackLanguage.Korean => ("kor", "ko"),
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        };
        var candidate = !string.IsNullOrWhiteSpace(track.LanguageIetf)
                        && !string.Equals(track.LanguageIetf, "und", StringComparison.OrdinalIgnoreCase)
            ? track.LanguageIetf
            : track.Language;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }
        return string.Equals(candidate, legacy, StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidate, ietf, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith($"{ietf}-", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetLanguageDisplayName(AudioTrackLanguage language) => language switch
    {
        AudioTrackLanguage.English => CoreText.Get("Language_English"),
        AudioTrackLanguage.Japanese => CoreText.Get("Language_Japanese"),
        AudioTrackLanguage.Korean => CoreText.Get("Language_Korean"),
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };

    private sealed record IndexedAudioTrack(MkvTrackInfo Track, int AudioIndex);
}

public sealed class BundledFfmpegAudioTranscoder(
    IProcessRunner processRunner,
    BundledFfmpegProvider? provider = null) : IAudioTranscoder
{
    private readonly BundledFfmpegProvider _provider = provider ?? new BundledFfmpegProvider();

    public async Task<AudioTranscodeResult> TranscodeAsync(
        AudioTranscodeRequest request,
        Action<int>? onProgress = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var executable = await _provider.GetExecutablePathAsync(cancellationToken).ConfigureAwait(false);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "repeat+level+warning", "-nostdin", "-y", "-xerror", "-copyts",
            "-i", request.SourcePath,
            "-map", $"0:a:{request.SourceAudioIndex}",
            "-vn", "-sn", "-dn",
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-c:a", request.Codec == AudioCodec.Opus ? "libopus" : "aac",
            "-b:a", $"{request.BitrateKbps.ToString(CultureInfo.InvariantCulture)}k"
        };
        if (request.OutputChannels.HasValue)
        {
            arguments.Add("-ac");
            arguments.Add(request.OutputChannels.Value.ToString(CultureInfo.InvariantCulture));
        }
        arguments.AddRange(["-avoid_negative_ts", "disabled", "-f", "matroska", "-progress", "pipe:1", "-nostats", request.OutputPath]);

        var durationMicroseconds = request.SourceDurationNanoseconds.HasValue
            ? request.SourceDurationNanoseconds.Value / 1000d
            : 0d;
        void HandleOutput(string line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                if (durationMicroseconds > 0
                    && long.TryParse(
                        line.AsSpan("out_time_us=".Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var current))
                {
                    onProgress?.Invoke(Math.Clamp((int)Math.Round(current / durationMicroseconds * 100d), 0, 100));
                }
                return;
            }

            // Other lines are buffered by the process runner. They are filtered and
            // forwarded only after a successful transcode.
        }

        var result = await processRunner.RunAsync(
            new ProcessRequest(executable, arguments, Path.GetDirectoryName(request.OutputPath)),
            HandleOutput,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(request.OutputPath) || new FileInfo(request.OutputPath).Length == 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException(CoreText.Get("Ffmpeg_TranscodeFailed", result.ExitCode, details.Trim()));
        }

        var warnings = FilterWarnings(result.StandardError);
        foreach (var warning in warnings)
        {
            onOutput?.Invoke(warning);
        }
        return new AudioTranscodeResult(warnings);
    }

    internal static IReadOnlyList<string> FilterWarnings(string standardError) =>
        ExternalToolDiagnostics.FilterSuccessfulProbeNoise(standardError);

}

public sealed class BundledFfmpegProvider
{
    public const string Version = "8.1";
    private const string ResourcePrefix = "SubMuxBatch.Core.Resources.ffmpeg.";
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);

    public static string InstallDirectory => Path.Combine(
        AppSettings.SettingsDirectory,
        "tools",
        "ffmpeg",
        Version);

    public static string ExecutablePath
    {
        get
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => throw new PlatformNotSupportedException(CoreText.Get("Ffmpeg_UnsupportedArchitecture"))
            };
            return Path.Combine(InstallDirectory, architecture, "ffmpeg.exe");
        }
    }

    public async Task<string> GetExecutablePathAsync(CancellationToken cancellationToken = default)
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException(CoreText.Get("Ffmpeg_UnsupportedArchitecture"))
        };
        var assembly = typeof(BundledFfmpegProvider).Assembly;
        var resourceName = ResourcePrefix + architecture + ".exe";
        byte[] expectedHash;
        await using (var resource = assembly.GetManifestResourceStream(resourceName)
                                  ?? throw new InvalidOperationException(CoreText.Get("Ffmpeg_BundledMissing")))
        {
            expectedHash = await SHA256.HashDataAsync(resource, cancellationToken).ConfigureAwait(false);
        }
        var directory = Path.Combine(InstallDirectory, architecture);
        Directory.CreateDirectory(directory);
        var destination = ExecutablePath;
        await ExtractionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(destination)
                && await MatchesHashAsync(destination, expectedHash, cancellationToken).ConfigureAwait(false))
            {
                return destination;
            }

            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using var resource = assembly.GetManifestResourceStream(resourceName)
                                           ?? throw new InvalidOperationException(CoreText.Get("Ffmpeg_BundledMissing"));
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
                if (!await MatchesHashAsync(temporary, expectedHash, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The extracted FFmpeg executable failed SHA-256 verification.");
                }
                File.Move(temporary, destination, overwrite: true);
                return destination;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            ExtractionGate.Release();
        }
    }

    public Task<string> EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
        GetExecutablePathAsync(cancellationToken);

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
}
