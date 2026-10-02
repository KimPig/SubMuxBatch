using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.External;

/// <summary>
/// Removes redundant static font, colour and size overrides from ASS generated
/// by the SRT conversion pipeline. Literal subtitle text is copied verbatim.
/// </summary>
public static partial class AssInlineTagOptimizer
{
    public static string OptimizeGeneratedAss(string ass)
    {
        ArgumentNullException.ThrowIfNull(ass);
        var defaultState = ReadDefaultState(ass);
        var newLine = ass.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : ass.Contains('\r') ? "\r"
            : "\n";
        var lines = Regex.Split(ass, @"\r\n|\n|\r");
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].TrimStart().StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = OptimizeDialogue(lines[index], defaultState);
            }
        }

        return string.Join(newLine, lines);
    }

    private static string OptimizeDialogue(string line, StyleState defaultState)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return line;
        var contentStart = colon + 1;
        while (contentStart < line.Length && char.IsWhiteSpace(line[contentStart])) contentStart++;
        var fields = SplitDialogueFields(line[contentStart..]);
        if (fields is null) return line;

        var text = fields[9];
        var blocks = OverrideBlockRegex().Matches(text);
        if (blocks.Count == 0 || blocks.Any(static block => UnsafeBlockRegex().IsMatch(block.Groups["body"].Value)))
        {
            return line;
        }

        var output = new StringBuilder(text.Length);
        var current = defaultState;
        var pending = current;
        var pendingOtherTags = new StringBuilder();
        var position = 0;

        foreach (Match block in blocks)
        {
            var literal = text[position..block.Index];
            if (literal.Length > 0)
            {
                AppendOptimizedBlock(output, pendingOtherTags, current, pending, defaultState);
                output.Append(literal);
                current = pending;
                pendingOtherTags.Clear();
            }

            var body = block.Groups["body"].Value;
            var matches = SupportedTagRegex().Matches(body);
            if (matches.Count == 0)
            {
                pendingOtherTags.Append(body);
            }
            else
            {
                var bodyPosition = 0;
                foreach (Match tag in matches)
                {
                    pendingOtherTags.Append(body, bodyPosition, tag.Index - bodyPosition);
                    pending = ApplyTag(pending, defaultState, tag);
                    bodyPosition = tag.Index + tag.Length;
                }
                pendingOtherTags.Append(body, bodyPosition, body.Length - bodyPosition);
            }

            position = block.Index + block.Length;
        }

        var tail = text[position..];
        if (tail.Length > 0)
        {
            AppendOptimizedBlock(output, pendingOtherTags, current, pending, defaultState);
            output.Append(tail);
        }
        else if (pendingOtherTags.Length > 0)
        {
            // Static state changes at the end of a Dialogue affect no text. Keep
            // unrelated commands, but discard font-only resets and repetitions.
            output.Append('{').Append(pendingOtherTags).Append('}');
        }

        fields[9] = output.ToString();
        return line[..contentStart] + string.Join(',', fields);
    }

    private static void AppendOptimizedBlock(
        StringBuilder output,
        StringBuilder otherTags,
        StyleState current,
        StyleState target,
        StyleState defaults)
    {
        var body = new StringBuilder(otherTags.ToString());
        if (!string.Equals(current.FontName, target.FontName, StringComparison.OrdinalIgnoreCase))
        {
            body.Append("\\fn");
            if (!string.Equals(target.FontName, defaults.FontName, StringComparison.OrdinalIgnoreCase))
            {
                body.Append(target.FontName);
            }
        }
        if (!string.Equals(current.Colour, target.Colour, StringComparison.OrdinalIgnoreCase))
        {
            body.Append("\\c");
            if (!string.Equals(target.Colour, defaults.Colour, StringComparison.OrdinalIgnoreCase))
            {
                body.Append(target.Colour);
            }
        }
        if (Math.Abs(current.FontSize - target.FontSize) >= 0.001)
        {
            body.Append("\\fs").Append(target.FontSize.ToString("0.###", CultureInfo.InvariantCulture));
        }
        if (body.Length > 0)
        {
            output.Append('{').Append(body).Append('}');
        }
    }

    private static StyleState ApplyTag(StyleState current, StyleState defaults, Match tag)
    {
        if (tag.Groups["font"].Success)
        {
            var value = tag.Groups["fontValue"].Value.Trim();
            return current with { FontName = value.Length == 0 ? defaults.FontName : value };
        }
        if (tag.Groups["size"].Success)
        {
            var value = tag.Groups["sizeValue"].Value;
            return current with
            {
                FontSize = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
                    ? size
                    : defaults.FontSize
            };
        }

        var colour = NormalizeColour(tag.Groups["colourValue"].Value);
        return current with { Colour = colour ?? defaults.Colour };
    }

    private static StyleState ReadDefaultState(string ass)
    {
        foreach (var line in Regex.Split(ass, @"\r\n|\n|\r"))
        {
            if (AssStyleDefinition.TryParse(line.Trim(), out var style)
                && string.Equals(style!.Name, "Default", StringComparison.Ordinal))
            {
                return new StyleState(
                    style.FontName.Trim(),
                    NormalizeColour(style.PrimaryColour) ?? "&HFFFFFF&",
                    style.FontSize);
            }
        }
        return new StyleState(string.Empty, "&HFFFFFF&", 20d);
    }

    private static string? NormalizeColour(string value)
    {
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length < 6) return null;
        return $"&H{hex[^6..].ToUpperInvariant()}&";
    }

    private static string[]? SplitDialogueFields(string content)
    {
        var fields = new string[10];
        var start = 0;
        for (var index = 0; index < 9; index++)
        {
            var comma = content.IndexOf(',', start);
            if (comma < 0) return null;
            fields[index] = content[start..comma];
            start = comma + 1;
        }
        fields[9] = content[start..];
        return fields;
    }

    private sealed record StyleState(string FontName, string Colour, double FontSize);

    [GeneratedRegex(@"\{(?<body>[^{}]*)\}")]
    private static partial Regex OverrideBlockRegex();

    [GeneratedRegex(@"\\(?:t|r)", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeBlockRegex();

    [GeneratedRegex(
        @"\\(?<font>fn)(?<fontValue>[^\\}]*)|\\(?<size>fs)(?![A-Za-z])(?<sizeValue>[+-]?(?:\d+(?:\.\d*)?|\.\d+)?)|\\(?<colour>1?c)(?![A-Za-z])(?<colourValue>&H[0-9A-Fa-f]{0,8}&?)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex SupportedTagRegex();
}
