using System.Security.Cryptography;
using System.Text.Json;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.External;

public enum LapseVerdict
{
    Solid,
    Unsure,
    Nothing,
    Failed
}

public sealed record LapseReferenceSelection(
    string Description,
    int? SubtitleOrdinal,
    int? AudioOrdinal,
    bool AudioOnly,
    bool RequireEmbeddedSubtitle = false);

public sealed record LapseSyncRequest(
    string ReferenceMediaPath,
    string SubtitlePath,
    string OutputPath,
    LapseSyncMode Mode,
    int SplitPenalty,
    LapseReferenceSelection Reference,
    int ConfidenceThreshold = 8);

public sealed record LapseSyncResult(
    LapseVerdict Verdict,
    string Mode,
    string Reference,
    long? OffsetMilliseconds,
    double? Ratio,
    double? Confidence,
    int Parts,
    IReadOnlyList<int> Splits,
    string? OutputPath,
    string? Error)
{
    public bool Applied => Verdict == LapseVerdict.Solid && OutputPath is not null;
    public long? MaximumAdjustmentMilliseconds { get; init; }

    public bool ShouldWarnAboutAutoStrategy(LapseSyncMode requestedMode) =>
        requestedMode == LapseSyncMode.Auto
        && !string.Equals(Mode, "auto/shifted", StringComparison.OrdinalIgnoreCase);
}

public interface ILapseSynchronizer
{
    Task<LapseSyncResult> SynchronizeAsync(
        LapseSyncRequest request,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default);
}

public sealed class BundledLapseSynchronizer(
    IProcessRunner processRunner,
    BundledLapseProvider? provider = null) : ILapseSynchronizer
{
    private readonly BundledLapseProvider _provider = provider ?? new BundledLapseProvider();

    public async Task<LapseSyncResult> SynchronizeAsync(
        LapseSyncRequest request,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var executable = await _provider.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var arguments = new List<string>
        {
            request.ReferenceMediaPath,
            request.SubtitlePath
        };
        switch (request.Mode)
        {
            case LapseSyncMode.Auto:
                break;
            case LapseSyncMode.NoSplit:
                arguments.Add("nosplit");
                break;
            case LapseSyncMode.Ols:
                arguments.Add("ols");
                break;
            case LapseSyncMode.Split:
                arguments.Add("split");
                arguments.Add(request.SplitPenalty.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Mode));
        }

        arguments.AddRange([
            "--json", "--strict", "--confidence",
            request.ConfidenceThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--output", request.OutputPath, "--no-backup", "--encoding", "utf8"]);
        if (request.Reference.AudioOnly) arguments.Add("--no-embedded");
        if (request.Reference.SubtitleOrdinal is { } subtitleOrdinal)
        {
            arguments.Add("--sub-track");
            arguments.Add(subtitleOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (request.Reference.AudioOrdinal is { } audioOrdinal)
        {
            arguments.Add("--audio-track");
            arguments.Add(audioOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        ProcessResult processResult;
        try
        {
            processResult = await processRunner.RunAsync(
                new ProcessRequest(executable, arguments, Path.GetDirectoryName(request.OutputPath)),
                onOutput: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(exception.Message);
        }

        var report = TryParseReport(processResult.StandardOutput);
        if (report is null)
        {
            return Failed(string.IsNullOrWhiteSpace(processResult.StandardError)
                ? $"LAPSE exited with code {processResult.ExitCode}."
                : processResult.StandardError.Trim());
        }

        var verdict = report.Verdict?.ToLowerInvariant() switch
        {
            "solid" => LapseVerdict.Solid,
            "unsure" => LapseVerdict.Unsure,
            "nothing" => LapseVerdict.Nothing,
            _ => LapseVerdict.Failed
        };
        if (request.Reference.RequireEmbeddedSubtitle
            && !string.Equals(report.Reference, "embedded", StringComparison.OrdinalIgnoreCase))
        {
            return Failed("LAPSE could not use the required embedded subtitle track as its reference.");
        }
        var outputExists = File.Exists(request.OutputPath) && new FileInfo(request.OutputPath).Length > 0;
        if (verdict == LapseVerdict.Solid && (!report.Written || !outputExists))
        {
            verdict = LapseVerdict.Failed;
        }

        if (verdict != LapseVerdict.Failed)
        {
            var standardOutputDiagnostics = processResult.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !IsLapseReportLine(line));
            var diagnostics = ExternalToolDiagnostics.FilterSuccessfulProbeNoise(
                string.Join(Environment.NewLine, standardOutputDiagnostics),
                processResult.StandardError);
            foreach (var diagnostic in diagnostics)
            {
                onOutput?.Invoke(diagnostic);
            }
        }

        return new LapseSyncResult(
            verdict,
            report.Mode ?? request.Mode.ToString(),
            report.Reference ?? request.Reference.Description,
            report.OffsetMs,
            report.Ratio,
            report.Confidence,
            report.Parts,
            report.Splits ?? [],
            verdict == LapseVerdict.Solid && outputExists ? request.OutputPath : null,
            verdict == LapseVerdict.Failed
                ? (string.IsNullOrWhiteSpace(processResult.StandardError) ? "LAPSE did not produce a valid solid result." : processResult.StandardError.Trim())
                : null);
    }

    private static LapseSyncResult Failed(string error) =>
        new(LapseVerdict.Failed, "—", "—", null, null, null, 0, [], null, error);

    private static LapseJsonReport? TryParseReport(string output)
    {
        foreach (var line in output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                return JsonSerializer.Deserialize<LapseJsonReport>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // LAPSE may emit non-JSON diagnostics before its final report.
            }
        }
        return null;
    }

    private static bool IsLapseReportLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) return false;
        try
        {
            var report = JsonSerializer.Deserialize<LapseJsonReport>(line, JsonOptions);
            return report is not null
                   && !string.IsNullOrWhiteSpace(report.Mode)
                   && !string.IsNullOrWhiteSpace(report.Verdict);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private sealed record LapseJsonReport(
        string? Mode,
        string? Reference,
        long? OffsetMs,
        double? Ratio,
        double? Confidence,
        string? Verdict,
        int Parts,
        bool Written,
        IReadOnlyList<int>? Splits);
}

public static class LapseReferenceSelector
{
    private static readonly HashSet<string> SupportedSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "S_TEXT/UTF8",
        "S_TEXT/ASS",
        "S_TEXT/SSA",
        "S_TEXT/WEBVTT",
        "S_TEXT/ASCII",
        "S_HDMV/PGS",
        "S_VOBSUB"
    };

    private static readonly HashSet<string> ManagedAssTrackNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "스타일 자막 (ASS)",
        "Styled subtitles (ASS)"
    };

    private static readonly HashSet<string> ManagedSrtTrackNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "일반 자막 (SRT)",
        "Standard subtitles (SRT)"
    };

    public static IReadOnlySet<int> FindSubMuxManagedSubtitleTrackIds(MkvInspection inspection)
    {
        var subtitles = inspection.Tracks
            .Where(static track => string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase)
                                   && track.Id.HasValue)
            .ToArray();
        var managedAss = subtitles.Where(track => ManagedAssTrackNames.Contains(track.TrackName ?? string.Empty)).ToArray();
        var managedSrt = subtitles.Where(track => ManagedSrtTrackNames.Contains(track.TrackName ?? string.Empty)).ToArray();
        return managedAss.Concat(managedSrt)
            .Select(static track => track.Id!.Value)
            .ToHashSet();
    }

    public static LapseReferenceSelection Select(
        MkvInspection inspection,
        AppSettings settings,
        IReadOnlySet<int>? excludedSubtitleTrackIds = null)
    {
        var subtitles = inspection.Tracks
            .Where(static track => string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase))
            .Select((track, ordinal) => new IndexedTrack(track, ordinal))
            .Where(item => item.Track.Id is null
                           || excludedSubtitleTrackIds is null
                           || !excludedSubtitleTrackIds.Contains(item.Track.Id.Value))
            .ToArray();
        var audio = inspection.Tracks
            .Where(static track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase))
            .Select((track, ordinal) => new IndexedTrack(track, ordinal))
            .ToArray();

        var desiredLanguage = settings.FilterAudioTracksByLanguage
            ? settings.SelectedAudioLanguage switch
            {
                AudioTrackLanguage.Japanese => new[] { "ja", "jpn", "japanese" },
                AudioTrackLanguage.Korean => new[] { "ko", "kor", "korean" },
                AudioTrackLanguage.English => new[] { "en", "eng", "english" },
                _ => []
            }
            : [];

        var selectedAudio = SelectAudioTrack(audio, desiredLanguage);
        var selectedSubtitle = settings.LapseReference == LapseReferenceMode.AudioOnly
            ? null
            : subtitles
                .Where(static item => IsNormalSubtitle(item.Track))
                .OrderByDescending(static item => item.Track.DefaultTrack)
                .ThenBy(static item => item.Ordinal)
                .FirstOrDefault();

        if (selectedSubtitle is not null)
        {
            var language = selectedSubtitle.Track.LanguageIetf
                           ?? selectedSubtitle.Track.Language
                           ?? "und";
            var defaultSuffix = selectedSubtitle.Track.DefaultTrack
                ? CoreText.Get("Lapse_DefaultTrackSuffix")
                : string.Empty;
            return new LapseReferenceSelection(
                CoreText.Get("Lapse_EmbeddedReference", selectedSubtitle.Ordinal, language, defaultSuffix),
                selectedSubtitle.Ordinal,
                selectedAudio?.Ordinal,
                false);
        }

        if (selectedAudio is null)
        {
            throw new InvalidOperationException("No audio track was found for LAPSE.");
        }

        var audioLanguage = selectedAudio.Track.LanguageIetf
                            ?? selectedAudio.Track.Language
                            ?? "und";
        var audioDefaultSuffix = selectedAudio.Track.DefaultTrack
            ? CoreText.Get("Lapse_DefaultTrackSuffix")
            : string.Empty;
        return new LapseReferenceSelection(
            CoreText.Get("Lapse_AudioReference", selectedAudio.Ordinal, audioLanguage, audioDefaultSuffix),
            null,
            selectedAudio.Ordinal,
            true);
    }

    private static bool IsNormalSubtitle(MkvTrackInfo track)
    {
        if (!SupportedSubtitleCodecs.Contains(track.CodecId)
            || track.ForcedTrack
            || track.HearingImpaired
            || track.Commentary)
        {
            return false;
        }

        var name = track.TrackName ?? string.Empty;
        return !new[] { "sign", "song", "commentary", "forced", "karaoke", "간판", "노래", "해설" }
            .Any(value => name.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static IndexedTrack? SelectAudioTrack(
        IReadOnlyList<IndexedTrack> audio,
        IReadOnlyList<string> desiredLanguage)
    {
        IndexedTrack? selected = null;
        if (desiredLanguage.Count > 0)
        {
            selected = audio
                .Where(item => LanguageMatches(item.Track, desiredLanguage))
                .OrderByDescending(static item => item.Track.DefaultTrack)
                .FirstOrDefault();
        }

        return selected
               ?? audio.FirstOrDefault(static item => item.Track.DefaultTrack)
               ?? audio.FirstOrDefault();
    }

    private static bool LanguageMatches(MkvTrackInfo track, IReadOnlyList<string> languages)
    {
        var values = new[] { track.LanguageIetf, track.Language };
        return values.Any(value => value is not null && languages.Any(language =>
            string.Equals(value, language, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed record IndexedTrack(MkvTrackInfo Track, int Ordinal);
}
