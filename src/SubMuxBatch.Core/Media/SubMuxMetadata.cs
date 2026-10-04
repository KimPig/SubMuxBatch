using System.Reflection;
using System.Xml.Linq;

namespace SubMuxBatch.Core.Media;

public static class SubMuxMetadata
{
    public const string VersionTagName = "SUBMUX_BATCH_VERSION";
    public const string ProcessedTagName = "SUBMUX_BATCH_PROCESSED";
    public const string SubtitleSourceTagName = "SUBMUX_SUBTITLE_SOURCE";
    public const string LegacySrtOrSmiSource = "LEGACY_SRT_OR_SMI";
    public const string LegacyAssOrUnknownSource = "LEGACY_ASS_OR_UNKNOWN";
    public const string ProcessedValue = "Processed by SubMux Batch";
    public const string LegacyCommentTagName = "COMMENT";
    private const string SubtitleSourceMarkerPrefix = "; SUBMUX_SUBTITLE_SOURCE=";
    private static readonly string[] LapseMarkerNames =
    [
        "SUBMUX_LAPSE_SYNC",
        "SUBMUX_LAPSE_VERSION",
        "SUBMUX_LAPSE_MODE",
        "SUBMUX_LAPSE_PROFILE",
        "SUBMUX_LAPSE_REFERENCE",
        "SUBMUX_LAPSE_RESULT",
        "SUBMUX_LAPSE_OFFSET_MS",
        "SUBMUX_LAPSE_RATIO",
        "SUBMUX_LAPSE_CONFIDENCE",
        "SUBMUX_LAPSE_SOURCE_SHA256"
    ];

    private static readonly HashSet<string> ValidSubtitleSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASS",
        "SRT",
        "SMI",
        "ASS+SRT",
        "ASS+SMI",
        LegacySrtOrSmiSource,
        LegacyAssOrUnknownSource
    };

    public static string CreateGlobalTagsXml(string? version = null) => new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement("Tags",
                new XElement("Tag",
                    new XElement("Targets"),
                    new XElement("Simple",
                        new XElement("Name", VersionTagName),
                        new XElement("String", NormalizeVersion(version) ?? GetApplicationVersion())),
                    new XElement("Simple",
                        new XElement("Name", ProcessedTagName),
                        new XElement("String", ProcessedValue)))))
        .ToString();

    public static string GetApplicationVersion()
    {
        var entryAssemblyVersion = GetInformationalVersion(Assembly.GetEntryAssembly());
        if (entryAssemblyVersion is not null)
        {
            return entryAssemblyVersion;
        }

        return GetInformationalVersion(typeof(SubMuxMetadata).Assembly)
               ?? typeof(SubMuxMetadata).Assembly.GetName().Version?.ToString(3)
               ?? "Unknown";
    }

    public static bool IsProcessed(
        string? version,
        string? processedMarker,
        string? legacyComment = null) =>
        !string.IsNullOrWhiteSpace(version)
        || !string.IsNullOrWhiteSpace(processedMarker)
        || legacyComment?.Contains(ProcessedValue, StringComparison.OrdinalIgnoreCase) == true;

    public static string? ReadSubtitleSourceMarker(string assText)
    {
        ArgumentNullException.ThrowIfNull(assText);
        var values = ReadScriptInfoValues(assText, SubtitleSourceMarkerPrefix)
            .Select(static value => value.ToUpperInvariant())
            .Where(ValidSubtitleSources.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    public static bool HasSubtitleSourceMarker(string assText)
    {
        ArgumentNullException.ThrowIfNull(assText);
        return ReadScriptInfoValues(assText, SubtitleSourceMarkerPrefix).Count > 0;
    }

    public static string? ReadAssLapseProfile(string assText)
    {
        ArgumentNullException.ThrowIfNull(assText);
        var values = ReadScriptInfoValues(assText, "; SUBMUX_LAPSE_PROFILE=")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    public static string AddOrReplaceSubtitleSourceMarker(string assText, string source)
    {
        ArgumentNullException.ThrowIfNull(assText);
        var normalizedSource = source.Trim().ToUpperInvariant();
        if (!ValidSubtitleSources.Contains(normalizedSource))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported subtitle source marker.");
        }

        var newline = assText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = assText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var scriptInfoIndex = lines.FindIndex(static line =>
            string.Equals(line.Trim(), "[Script Info]", StringComparison.OrdinalIgnoreCase));
        if (scriptInfoIndex >= 0)
        {
            RemoveScriptInfoMarkers(lines, scriptInfoIndex, [SubtitleSourceMarkerPrefix]);
        }
        var marker = $"{SubtitleSourceMarkerPrefix}{normalizedSource}";
        if (scriptInfoIndex >= 0)
        {
            lines.Insert(scriptInfoIndex + 1, marker);
        }
        else
        {
            lines.Insert(0, marker);
            lines.Insert(0, "[Script Info]");
        }

        return string.Join(newline, lines);
    }

    public static string RemoveSubtitleSourceMarker(string assText)
    {
        ArgumentNullException.ThrowIfNull(assText);
        var newline = assText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = assText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var scriptInfoIndex = lines.FindIndex(static line =>
            string.Equals(line.Trim(), "[Script Info]", StringComparison.OrdinalIgnoreCase));
        if (scriptInfoIndex >= 0)
        {
            RemoveScriptInfoMarkers(lines, scriptInfoIndex, [SubtitleSourceMarkerPrefix]);
        }
        return string.Join(newline, lines);
    }

    public static bool HasAssLapseMarker(string assText) =>
        ReadScriptInfoValues(assText, "; SUBMUX_LAPSE_SYNC=").Count > 0;

    public static string AddOrReplaceAssLapseMarker(
        string assText,
        string mode,
        string result,
        string sourceSha256,
        string? applicationVersion = null,
        string? lapseVersion = null,
        string? profile = null,
        string? reference = null,
        long? offsetMilliseconds = null,
        double? ratio = null,
        double? confidence = null)
    {
        ArgumentNullException.ThrowIfNull(assText);
        var newline = assText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = assText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var scriptInfo = lines.FindIndex(line => string.Equals(line.Trim(), "[Script Info]", StringComparison.OrdinalIgnoreCase));
        if (scriptInfo < 0)
        {
            lines.Insert(0, "[Script Info]");
            scriptInfo = 0;
        }
        RemoveScriptInfoMarkers(
            lines,
            scriptInfo,
            LapseMarkerNames.Select(static name => $"; {name}=").ToArray());
        var markers = new[]
        {
            $"; SUBMUX_LAPSE_SYNC={applicationVersion ?? GetApplicationVersion()}",
            $"; SUBMUX_LAPSE_VERSION={lapseVersion ?? Dependencies.BundledLapseProvider.Version}",
            $"; SUBMUX_LAPSE_MODE={mode}",
            profile is null ? null : $"; SUBMUX_LAPSE_PROFILE={profile}",
            reference is null ? null : $"; SUBMUX_LAPSE_REFERENCE={SubMuxBatch.Core.External.LapseSubtitleMetadata.NormalizeReference(reference)}",
            $"; SUBMUX_LAPSE_RESULT={result}",
            offsetMilliseconds is null ? null : $"; SUBMUX_LAPSE_OFFSET_MS={offsetMilliseconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            ratio is null ? null : $"; SUBMUX_LAPSE_RATIO={ratio.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
            confidence is null ? null : $"; SUBMUX_LAPSE_CONFIDENCE={confidence.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
            $"; SUBMUX_LAPSE_SOURCE_SHA256={sourceSha256}"
        }.Where(static marker => marker is not null).Select(static marker => marker!).ToArray();
        lines.InsertRange(scriptInfo + 1, markers);
        return string.Join(newline, lines);
    }

    public static string CopyAssLapseMarkers(string sourceAssText, string targetAssText)
    {
        ArgumentNullException.ThrowIfNull(sourceAssText);
        ArgumentNullException.ThrowIfNull(targetAssText);
        var markerPrefixes = LapseMarkerNames.Select(static name => $"; {name}=").ToArray();
        var sourceLines = sourceAssText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sourceMarkers = markerPrefixes
            .SelectMany(prefix => ReadScriptInfoLines(sourceLines, prefix))
            .ToArray();
        if (sourceMarkers.Length == 0) return targetAssText;

        var newline = targetAssText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var targetLines = targetAssText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var scriptInfo = targetLines.FindIndex(static line =>
            string.Equals(line.Trim(), "[Script Info]", StringComparison.OrdinalIgnoreCase));
        if (scriptInfo < 0)
        {
            targetLines.Insert(0, "[Script Info]");
            scriptInfo = 0;
        }
        RemoveScriptInfoMarkers(targetLines, scriptInfo, markerPrefixes);
        targetLines.InsertRange(scriptInfo + 1, sourceMarkers);
        return string.Join(newline, targetLines);
    }

    private static IReadOnlyList<string> ReadScriptInfoValues(string assText, string prefix)
    {
        var result = new List<string>();
        var inScriptInfo = false;
        foreach (var line in assText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith(']'))
            {
                inScriptInfo = string.Equals(trimmed, "[Script Info]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inScriptInfo || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(trimmed[prefix.Length..].Trim());
        }
        return result;
    }

    private static IReadOnlyList<string> ReadScriptInfoLines(IEnumerable<string> lines, string prefix)
    {
        var result = new List<string>();
        var inScriptInfo = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith(']'))
            {
                inScriptInfo = string.Equals(trimmed, "[Script Info]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inScriptInfo && trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(trimmed);
            }
        }
        return result;
    }

    private static void RemoveScriptInfoMarkers(List<string> lines, int scriptInfoIndex, IReadOnlyList<string> prefixes)
    {
        var end = lines.FindIndex(scriptInfoIndex + 1, static line =>
        {
            var trimmed = line.Trim();
            return trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith(']');
        });
        if (end < 0) end = lines.Count;
        for (var index = end - 1; index > scriptInfoIndex; index--)
        {
            var trimmed = lines[index].TrimStart();
            if (prefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                lines.RemoveAt(index);
            }
        }
    }

    private static string? GetInformationalVersion(Assembly? assembly) =>
        NormalizeVersion(assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion);

    private static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var normalized = version.Trim();
        if (normalized.StartsWith('v'))
        {
            normalized = normalized[1..];
        }

        var metadataSeparator = normalized.IndexOf('+');
        return metadataSeparator >= 0
            ? normalized[..metadataSeparator]
            : normalized;
    }
}
