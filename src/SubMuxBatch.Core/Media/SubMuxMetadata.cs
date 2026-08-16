using System.Reflection;
using System.Xml.Linq;

namespace SubMuxBatch.Core.Media;

public static class SubMuxMetadata
{
    public const string VersionTagName = "SUBMUX_BATCH_VERSION";
    public const string CommentTagName = "COMMENT";
    public const string CommentValue = "Processed by SubMux Batch";

    public static string CreateGlobalTagsXml(string? version = null) => new XDocument(
        new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement("Tags",
            new XElement("Tag",
                new XElement("Targets"),
                new XElement("Simple",
                    new XElement("Name", VersionTagName),
                    new XElement("String", NormalizeVersion(version) ?? GetApplicationVersion())),
                new XElement("Simple",
                    new XElement("Name", CommentTagName),
                    new XElement("String", CommentValue)))))
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

    public static bool IsProcessed(string? version, string? comment) =>
        !string.IsNullOrWhiteSpace(version)
        || comment?.Contains(CommentValue, StringComparison.OrdinalIgnoreCase) == true;

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
