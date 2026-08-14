using System.Text.RegularExpressions;

namespace SubMuxBatch.Core.Fonts;

public static partial class AssFontNameExtractor
{
    public static IReadOnlyList<string> Extract(string assText)
    {
        if (string.IsNullOrWhiteSpace(assText))
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inStylesSection = false;
        var fontNameIndex = 1;
        using var reader = new StringReader(assText);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inStylesSection = trimmed.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)
                                  || trimmed.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inStylesSection && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = trimmed[7..].Split(',', StringSplitOptions.TrimEntries);
                var detectedIndex = Array.FindIndex(
                    fields,
                    static field => field.Equals("Fontname", StringComparison.OrdinalIgnoreCase));
                if (detectedIndex >= 0)
                {
                    fontNameIndex = detectedIndex;
                }
            }
            else if (inStylesSection && trimmed.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = trimmed[6..].Split(',', StringSplitOptions.TrimEntries);
                if (fontNameIndex < fields.Length && !string.IsNullOrWhiteSpace(fields[fontNameIndex]))
                {
                    names.Add(fields[fontNameIndex]);
                }
            }

            foreach (Match match in InlineFontNamePattern().Matches(line))
            {
                var name = match.Groups["name"].Value.Trim();
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
        }

        return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [GeneratedRegex(@"\\fn(?<name>[^\\}]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineFontNamePattern();
}
