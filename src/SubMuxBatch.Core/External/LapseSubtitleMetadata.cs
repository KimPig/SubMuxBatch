using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.External;

public static partial class LapseSubtitleMetadata
{
    public const string SrtMarkerPrefix = "__SUBMUX_LAPSE_SYNC_";
    private const char InvisibleSeparator = '\u2063';

    public static bool HasSrtMarker(string text) =>
        text.Contains(SrtMarkerPrefix, StringComparison.OrdinalIgnoreCase);

    public sealed record SrtMarkerInfo(
        string ApplicationVersion,
        string LapseVersion,
        string? SettingsProfile,
        string? Reference);

    public static SrtMarkerInfo? ReadSrtMarker(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = SrtMarkerRegex().Match(text);
        if (!match.Success) return null;
        var profile = match.Groups["profile"].Success
            ? match.Groups["profile"].Value.Replace('-', '|')
            : null;
        return new SrtMarkerInfo(
            match.Groups["app"].Value,
            match.Groups["lapse"].Value,
            profile,
            match.Groups["reference"].Success ? match.Groups["reference"].Value.ToUpperInvariant() : null);
    }

    public static string CreateSettingsProfile(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"{settings.LapseMode}|{settings.LapseReference}|{settings.LapseSplitPenalty}|{settings.LapseConfidenceThreshold}".ToUpperInvariant();
    }

    public static string RemoveSrtMarkers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var blocks = BlockSeparatorRegex().Split(NormalizeNewlines(text));
        return string.Join("\r\n\r\n", blocks.Where(block =>
                !block.Contains(SrtMarkerPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(static block => block.Replace("\n", "\r\n"))).TrimEnd() + "\r\n";
    }

    public static string AddSrtMarker(
        string text,
        long? videoDurationNanoseconds,
        string? appVersion = null,
        string? settingsProfile = null,
        string? reference = null)
    {
        var clean = RemoveSrtMarkers(text);
        var cues = ParseSrtCues(clean);
        var lastEnd = cues.Count == 0 ? 0 : cues.Max(static cue => cue.EndMilliseconds);
        var videoEnd = videoDurationNanoseconds is > 0
            ? (long)Math.Ceiling(videoDurationNanoseconds.Value / 1_000_000d)
            : 0;
        var start = Math.Max(lastEnd, videoEnd) + 1;
        var nextNumber = cues.Select(static cue => cue.Number).DefaultIfEmpty(0).Max() + 1;
        var version = NormalizeVersion(appVersion ?? SubMuxMetadata.GetApplicationVersion());
        var profileToken = string.IsNullOrWhiteSpace(settingsProfile)
            ? string.Empty
            : $"_PROFILE_{NormalizeMarkerToken(settingsProfile).Replace('|', '-')}";
        var referenceToken = string.IsNullOrWhiteSpace(reference)
            ? string.Empty
            : $"_REF_{NormalizeReference(reference)}";
        var marker = $"<font face=\"{SrtMarkerPrefix}{version}_LAPSE_{BundledLapseProvider.Version}{profileToken}{referenceToken}__\">{InvisibleSeparator}</font>";
        return clean.TrimEnd() + $"\r\n\r\n{nextNumber}\r\n{FormatTime(start)} --> {FormatTime(start + 1)}\r\n{marker}\r\n";
    }

    public static string NormalizeReference(string? reference) => reference?.Trim().ToLowerInvariant() switch
    {
        "embedded" => "EMBEDDED",
        "vad" or "audio" => "AUDIO",
        null or "" => "UNKNOWN",
        var value => NormalizeMarkerToken(value).ToUpperInvariant()
    };

    public static void ValidateTimingOnlyChange(string originalPath, string synchronizedPath)
    {
        var extension = Path.GetExtension(originalPath);
        var original = File.ReadAllText(originalPath);
        var synchronized = File.ReadAllText(synchronizedPath);
        if (string.Equals(extension, ".ass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".ssa", StringComparison.OrdinalIgnoreCase))
        {
            ValidateAssTimingOnly(original, synchronized);
            return;
        }

        var originalCues = ParseSrtCues(RemoveSrtMarkers(original));
        var synchronizedCues = ParseSrtCues(RemoveSrtMarkers(synchronized));
        if (originalCues.Count != synchronizedCues.Count)
        {
            throw new InvalidDataException("LAPSE changed the number of subtitle cues.");
        }
        for (var index = 0; index < originalCues.Count; index++)
        {
            if (!string.Equals(originalCues[index].Text, synchronizedCues[index].Text, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"LAPSE changed subtitle text at cue {index + 1}.");
            }
            if (synchronizedCues[index].StartMilliseconds < 0
                || synchronizedCues[index].EndMilliseconds <= synchronizedCues[index].StartMilliseconds)
            {
                throw new InvalidDataException($"LAPSE produced an invalid timestamp at cue {index + 1}.");
            }
        }
    }

    public static string ApplySrtTimingsToAss(string assText, string synchronizedSrt)
    {
        var cues = ParseSrtCues(RemoveSrtMarkers(synchronizedSrt));
        var newline = assText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = NormalizeNewlines(assText).Split('\n');
        var dialogueIndices = lines.Select((line, index) => (line, index))
            .Where(static item => item.line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.index)
            .ToArray();
        if (dialogueIndices.Length != cues.Count)
        {
            throw new InvalidDataException("The synchronized SRT cue count does not match the ASS dialogue count.");
        }

        for (var index = 0; index < cues.Count; index++)
        {
            var lineIndex = dialogueIndices[index];
            var fields = lines[lineIndex].Split(',', 10);
            if (fields.Length != 10) throw new InvalidDataException("Invalid ASS dialogue line.");
            fields[1] = FormatAssTime(cues[index].StartMilliseconds);
            fields[2] = FormatAssTime(cues[index].EndMilliseconds);
            lines[lineIndex] = string.Join(',', fields);
        }
        return string.Join(newline, lines);
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void ValidateAssTimingOnly(string original, string synchronized)
    {
        var left = GetAssSemanticLines(original);
        var right = GetAssSemanticLines(synchronized);
        if (left.Count != right.Count)
        {
            throw new InvalidDataException("LAPSE changed the number of ASS events.");
        }
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                throw new InvalidDataException($"LAPSE changed ASS content at event {index + 1}.");
            }
        }
    }

    private static List<string> GetAssSemanticLines(string text)
    {
        var result = new List<string>();
        foreach (var line in NormalizeNewlines(text).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var fields = trimmed.Split(',', 10);
            result.Add(fields.Length == 10
                ? "EVENT:" + string.Join(',', fields[0], fields[3], fields[4], fields[5], fields[6], fields[7], fields[8], fields[9])
                : "RAW:" + trimmed);
        }
        return result;
    }

    private static List<SrtCue> ParseSrtCues(string text)
    {
        var result = new List<SrtCue>();
        var lines = NormalizeNewlines(text).Split('\n');
        for (var timeIndex = 0; timeIndex < lines.Length; timeIndex++)
        {
            if (!TimeRegex().IsMatch(lines[timeIndex])) continue;
            var match = TimeRegex().Match(lines[timeIndex]);
            var number = timeIndex > 0
                         && int.TryParse(
                             lines[timeIndex - 1].Trim(),
                             NumberStyles.None,
                             CultureInfo.InvariantCulture,
                             out var parsed)
                ? parsed
                : 0;
            var nextTimeIndex = timeIndex + 1;
            while (nextTimeIndex < lines.Length && !TimeRegex().IsMatch(lines[nextTimeIndex]))
            {
                nextTimeIndex++;
            }
            var textEndExclusive = nextTimeIndex;
            if (nextTimeIndex < lines.Length
                && nextTimeIndex > timeIndex + 1
                && int.TryParse(
                    lines[nextTimeIndex - 1].Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _))
            {
                textEndExclusive--;
            }
            while (textEndExclusive > timeIndex + 1
                   && string.IsNullOrWhiteSpace(lines[textEndExclusive - 1]))
            {
                textEndExclusive--;
            }
            result.Add(new SrtCue(
                number,
                ParseTime(match, "sh", "sm", "ss", "sms"),
                ParseTime(match, "eh", "em", "es", "ems"),
                string.Join("\n", lines[(timeIndex + 1)..textEndExclusive])));
            timeIndex = nextTimeIndex - 1;
        }
        return result;
    }

    private static long ParseTime(Match match, string h, string m, string s, string ms) =>
        long.Parse(match.Groups[h].Value, CultureInfo.InvariantCulture) * 3_600_000
        + long.Parse(match.Groups[m].Value, CultureInfo.InvariantCulture) * 60_000
        + long.Parse(match.Groups[s].Value, CultureInfo.InvariantCulture) * 1000
        + long.Parse(match.Groups[ms].Value, CultureInfo.InvariantCulture);

    private static string FormatTime(long value)
    {
        var span = TimeSpan.FromMilliseconds(value);
        return $"{(long)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00},{span.Milliseconds:000}";
    }

    private static string FormatAssTime(long milliseconds)
    {
        var centiseconds = Math.Max(0, (long)Math.Round(milliseconds / 10d, MidpointRounding.AwayFromZero));
        var hours = centiseconds / 360_000;
        var minutes = centiseconds / 6_000 % 60;
        var seconds = centiseconds / 100 % 60;
        var fraction = centiseconds % 100;
        return $"{hours}:{minutes:00}:{seconds:00}.{fraction:00}";
    }

    private static string NormalizeVersion(string value) =>
        value.Trim().TrimStart('v').Split('+')[0];

    private static string NormalizeMarkerToken(string value) =>
        Regex.Replace(value.Trim(), @"[^A-Za-z0-9|.-]+", "-").Trim('-');

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private sealed record SrtCue(int Number, long StartMilliseconds, long EndMilliseconds, string Text);

    [GeneratedRegex(@"\n[ \t]*\n+")]
    private static partial Regex BlockSeparatorRegex();

    [GeneratedRegex(@"(?<sh>\d{1,3}):(?<sm>\d{2}):(?<ss>\d{2})[,.](?<sms>\d{3})\s*-->\s*(?<eh>\d{1,3}):(?<em>\d{2}):(?<es>\d{2})[,.](?<ems>\d{3})")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"__SUBMUX_LAPSE_SYNC_(?<app>[^_""\s]+)_LAPSE_(?<lapse>[^_""\s]+)(?:_PROFILE_(?<profile>.*?))?(?:_REF_(?<reference>[^_""\s]+))?__", RegexOptions.IgnoreCase)]
    private static partial Regex SrtMarkerRegex();
}

public sealed record ExternalSubtitleBackupIndex(
    string OriginalPath,
    string BackupFileName,
    DateTimeOffset BackupTime,
    string OriginalSha256,
    string ApplicationVersion,
    string LapseVersion,
    string Mode,
    string Verdict,
    long? OffsetMilliseconds,
    double? Ratio,
    string NewPath,
    string? Reference = null,
    double? Confidence = null,
    string? SettingsProfile = null);

public sealed class ExternalSubtitleReplacement(
    string originalPath,
    string synchronizedPath,
    LapseSyncResult result,
    long? videoDurationNanoseconds,
    bool addSrtMarker,
    string? settingsProfile = null)
{
    public async Task<string> CommitAsync(CancellationToken cancellationToken = default)
    {
        var sourceDirectory = Path.GetDirectoryName(originalPath)
                              ?? throw new InvalidOperationException("The subtitle path has no parent directory.");
        var backupRoot = Path.Combine(sourceDirectory, ".submux-backup", "external-subtitles");
        var indexRoot = Path.Combine(backupRoot, ".index");
        Directory.CreateDirectory(indexRoot);
        var backupPath = GetAvailablePath(Path.Combine(backupRoot, Path.GetFileName(originalPath)));
        var originalHash = LapseSubtitleMetadata.ComputeSha256(originalPath);
        File.Copy(originalPath, backupPath, overwrite: false);

        var replacementPath = string.Equals(Path.GetExtension(originalPath), ".smi", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(originalPath, ".srt")
            : originalPath;
        if (!string.Equals(replacementPath, originalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(replacementPath))
        {
            File.Delete(backupPath);
            throw new IOException($"The synchronized SRT already exists: {replacementPath}");
        }

        var staged = synchronizedPath + ".commit-" + Guid.NewGuid().ToString("N");
        var replacementCommitted = false;
        try
        {
            var text = await File.ReadAllTextAsync(synchronizedPath, cancellationToken).ConfigureAwait(false);
            if (addSrtMarker)
            {
                text = LapseSubtitleMetadata.AddSrtMarker(
                    text,
                    videoDurationNanoseconds,
                    settingsProfile: settingsProfile,
                    reference: result.Reference);
            }
            await File.WriteAllTextAsync(staged, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(staged, replacementPath, overwrite: true);
            replacementCommitted = true;

            var index = new ExternalSubtitleBackupIndex(
                Path.GetFullPath(originalPath),
                Path.GetFileName(backupPath),
                DateTimeOffset.Now,
                originalHash,
                SubMuxMetadata.GetApplicationVersion(),
                BundledLapseProvider.Version,
                result.Mode,
                result.Verdict.ToString().ToLowerInvariant(),
                result.OffsetMilliseconds,
                result.Ratio,
                Path.GetFullPath(replacementPath),
                LapseSubtitleMetadata.NormalizeReference(result.Reference),
                result.Confidence,
                settingsProfile);
            var jsonPath = Path.Combine(indexRoot, Path.GetFileName(backupPath) + ".json");
            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(replacementPath, originalPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(originalPath);
            }
            return replacementPath;
        }
        catch
        {
            if (File.Exists(staged)) File.Delete(staged);
            if (!replacementCommitted
                && File.Exists(backupPath)
                && !File.Exists(Path.Combine(indexRoot, Path.GetFileName(backupPath) + ".json")))
            {
                File.Delete(backupPath);
            }
            throw;
        }
    }

    private static string GetAvailablePath(string preferred)
    {
        if (!File.Exists(preferred)) return preferred;
        var directory = Path.GetDirectoryName(preferred)!;
        var stem = Path.GetFileNameWithoutExtension(preferred);
        var extension = Path.GetExtension(preferred);
        for (var index = 1; index < int.MaxValue; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not allocate an external subtitle backup name.");
    }
}
