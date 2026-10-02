using System.Reflection;
using System.Xml.Linq;

namespace SubMuxBatch.Core.Media;

public static class SubMuxMetadata
{
    public const string VersionTagName = "SUBMUX_BATCH_VERSION";
    public const string ProcessedTagName = "SUBMUX_BATCH_PROCESSED";
    public const string AssSourceTagName = "SUBMUX_ASS_SOURCE";
    public const string LegacySrtOrSmiSource = "LEGACY_SRT_OR_SMI";
    public const string LegacyAssOrUnknownSource = "LEGACY_ASS_OR_UNKNOWN";
    public const string ProcessedValue = "Processed by SubMux Batch";
    public const string LegacyCommentTagName = "COMMENT";
    private const string AssSourceMarkerPrefix = "; SUBMUX_ASS_SOURCE=";

    private static readonly HashSet<string> ValidAssSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASS",
        "SRT",
        "SMI",
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

    public static string? ReadAssSourceMarker(string assText)
    {
        ArgumentNullException.ThrowIfNull(assText);
        foreach (var line in assText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(AssSourceMarkerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed[AssSourceMarkerPrefix.Length..].Trim().ToUpperInvariant();
            return ValidAssSources.Contains(value) ? value : null;
        }

        return null;
    }

    public static string AddAssSourceMarker(string assText, string source)
    {
        ArgumentNullException.ThrowIfNull(assText);
        var normalizedSource = source.Trim().ToUpperInvariant();
        if (!ValidAssSources.Contains(normalizedSource))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported ASS source marker.");
        }
        if (ReadAssSourceMarker(assText) is not null)
        {
            return assText;
        }

        var newline = assText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = assText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var scriptInfoIndex = lines.FindIndex(static line =>
            string.Equals(line.Trim(), "[Script Info]", StringComparison.OrdinalIgnoreCase));
        var marker = $"{AssSourceMarkerPrefix}{normalizedSource}";
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
