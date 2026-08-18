using System.Globalization;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Fonts;

public sealed record AssFontRequirement(string FamilyName, int Weight, bool Italic);

public sealed class AssFontAnalysisException(string message) : Exception(message);

public static class AssFontNameExtractor
{
    private const int MaxStatesPerDialogue = 4096;
    private const int MaxTransformDepth = 32;
    private static readonly StyleFont BuiltInDefaultStyle = new("Arial", 400, false);

    public static IReadOnlyList<string> Extract(string assText) =>
        ExtractRequirements(assText)
            .Select(static requirement => requirement.FamilyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<AssFontRequirement> ExtractRequirements(string assText)
    {
        if (string.IsNullOrWhiteSpace(assText))
        {
            return [];
        }

        var lines = ReadLines(assText);
        var styles = ParseStyles(lines);
        var defaultStyle = styles
            .FirstOrDefault(static pair => pair.Key.Equals("Default", StringComparison.OrdinalIgnoreCase))
            .Value
            ?? BuiltInDefaultStyle;
        var requirements = new HashSet<AssFontRequirement>(AssFontRequirementComparer.Instance);
        ParseDialogueRequirements(lines, styles, defaultStyle, requirements);
        return requirements
            .OrderBy(static requirement => requirement.FamilyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static requirement => requirement.Weight)
            .ThenBy(static requirement => requirement.Italic)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static IReadOnlyDictionary<string, StyleFont> ParseStyles(IReadOnlyList<string> lines)
    {
        var styles = new Dictionary<string, StyleFont>(StringComparer.Ordinal);
        var inStylesSection = false;
        var nameIndex = 0;
        var fontNameIndex = 1;
        var boldIndex = 7;
        var italicIndex = 8;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (TryReadSection(trimmed, out var section))
            {
                inStylesSection = section.Equals("V4+ Styles", StringComparison.OrdinalIgnoreCase)
                                  || section.Equals("V4 Styles", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inStylesSection)
            {
                continue;
            }

            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = trimmed[7..].Split(',', StringSplitOptions.TrimEntries);
                nameIndex = FindField(fields, "Name", nameIndex);
                fontNameIndex = FindField(fields, "Fontname", fontNameIndex);
                boldIndex = FindField(fields, "Bold", boldIndex);
                italicIndex = FindField(fields, "Italic", italicIndex);
                continue;
            }

            if (!trimmed.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = trimmed[6..].Split(',', StringSplitOptions.TrimEntries);
            var styleName = GetValue(values, nameIndex).TrimStart('*');
            var familyName = GetValue(values, fontNameIndex);
            styles[styleName.Length == 0 ? "Default" : styleName] = new StyleFont(
                familyName.Length == 0 ? "Arial" : familyName,
                ReadStyleWeight(values, boldIndex),
                ReadStyleItalic(values, italicIndex));
        }

        return styles;
    }

    private static void ParseDialogueRequirements(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<string, StyleFont> styles,
        StyleFont defaultStyle,
        ISet<AssFontRequirement> requirements)
    {
        var inEventsSection = false;
        string[] eventFields = [];
        var styleIndex = 3;
        var textIndex = 9;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (TryReadSection(trimmed, out var section))
            {
                inEventsSection = section.Equals("Events", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEventsSection)
            {
                continue;
            }

            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                eventFields = trimmed[7..].Split(',', StringSplitOptions.TrimEntries);
                styleIndex = FindField(eventFields, "Style", styleIndex);
                textIndex = FindField(eventFields, "Text", textIndex);
                continue;
            }

            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fieldCount = eventFields.Length > 0 ? eventFields.Length : 10;
            var values = trimmed[9..].Split(',', fieldCount, StringSplitOptions.None);
            if (textIndex < 0 || textIndex >= values.Length)
            {
                continue;
            }

            var baseStyle = ResolveDialogueStyle(GetValue(values, styleIndex), styles, defaultStyle);
            ParseDialogueText(values[textIndex], baseStyle, styles, requirements);
        }
    }

    private static StyleFont ResolveDialogueStyle(
        string styleName,
        IReadOnlyDictionary<string, StyleFont> styles,
        StyleFont defaultStyle)
    {
        var normalized = styleName.Trim().TrimStart('*');
        if (normalized.Length == 0 || normalized.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return defaultStyle;
        }

        return styles.TryGetValue(normalized, out var style) ? style : defaultStyle;
    }

    private static void ParseDialogueText(
        string text,
        StyleFont baseStyle,
        IReadOnlyDictionary<string, StyleFont> styles,
        ISet<AssFontRequirement> requirements)
    {
        var states = new HashSet<RenderState> { RenderState.FromStyle(baseStyle) };
        var index = 0;
        while (index < text.Length)
        {
            var blockStart = text.IndexOf('{', index);
            if (blockStart < 0)
            {
                AddRenderedSegment(text.AsSpan(index), states, requirements);
                break;
            }

            AddRenderedSegment(text.AsSpan(index, blockStart - index), states, requirements);
            var blockEnd = text.IndexOf('}', blockStart + 1);
            if (blockEnd < 0)
            {
                AddRenderedSegment(text.AsSpan(blockStart), states, requirements);
                break;
            }

            ApplyTagSpan(
                text.AsSpan(blockStart + 1, blockEnd - blockStart - 1),
                states,
                baseStyle,
                styles,
                transformDepth: 0);
            index = blockEnd + 1;
        }
    }

    private static void AddRenderedSegment(
        ReadOnlySpan<char> text,
        IEnumerable<RenderState> states,
        ISet<AssFontRequirement> requirements)
    {
        if (!HasRenderableText(text))
        {
            return;
        }

        foreach (var state in states)
        {
            if (state.DrawingScale == 0 && !string.IsNullOrWhiteSpace(state.FamilyName))
            {
                requirements.Add(new AssFontRequirement(
                    state.FamilyName.Trim(),
                    state.Weight,
                    state.Italic));
            }
        }
    }

    private static bool HasRenderableText(ReadOnlySpan<char> text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                if (text[index + 1] is 'N' or 'n' or 'h')
                {
                    index++;
                    continue;
                }

                return true;
            }

            if (!char.IsWhiteSpace(text[index])
                && char.GetUnicodeCategory(text[index])
                    is not (UnicodeCategory.Control or UnicodeCategory.Format))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyTagSpan(
        ReadOnlySpan<char> tags,
        HashSet<RenderState> states,
        StyleFont baseStyle,
        IReadOnlyDictionary<string, StyleFont> styles,
        int transformDepth)
    {
        if (transformDepth > MaxTransformDepth)
        {
            throw new AssFontAnalysisException(CoreText.Get("Batch_FontTransformTooDeep"));
        }

        for (var tagStart = 0; tagStart < tags.Length; tagStart++)
        {
            if (tags[tagStart] != '\\')
            {
                continue;
            }

            var remainder = tags[(tagStart + 1)..];
            if (TryReadTransform(remainder, out var transformContent, out var transformLength))
            {
                var transformed = new HashSet<RenderState>(states);
                ApplyTagSpan(transformContent, transformed, baseStyle, styles, transformDepth + 1);
                states.UnionWith(transformed);
                EnsureStateLimit(states);
                tagStart += transformLength;
                continue;
            }

            var tag = ReadRelevantTag(remainder, out var tagLength);
            if (tag is null)
            {
                continue;
            }

            var valueStart = tagStart + 1 + tagLength;
            var valueEnd = valueStart;
            while (valueEnd < tags.Length && tags[valueEnd] != '\\')
            {
                valueEnd++;
            }

            var value = tags[valueStart..valueEnd].ToString().Trim();
            ReplaceStates(states, states.Select(state => ApplyTag(tag, value, state, baseStyle, styles)));
            EnsureStateLimit(states);
            tagStart = valueEnd - 1;
        }
    }

    private static void EnsureStateLimit(IReadOnlyCollection<RenderState> states)
    {
        if (states.Count > MaxStatesPerDialogue)
        {
            throw new AssFontAnalysisException(CoreText.Get("Batch_FontTooManyStates"));
        }
    }

    private static void ReplaceStates(HashSet<RenderState> states, IEnumerable<RenderState> replacements)
    {
        var values = replacements.ToArray();
        states.Clear();
        states.UnionWith(values);
    }

    private static string? ReadRelevantTag(ReadOnlySpan<char> value, out int length)
    {
        length = 0;
        if (value.StartsWith("fn", StringComparison.OrdinalIgnoreCase))
        {
            length = 2;
            return "fn";
        }

        if (value.IsEmpty)
        {
            return null;
        }

        var tag = char.ToLowerInvariant(value[0]);
        if (tag == 'r')
        {
            length = 1;
            return "r";
        }

        if (tag is not ('b' or 'i' or 'p') || value.Length > 1 && char.IsLetter(value[1]))
        {
            return null;
        }

        length = 1;
        return tag.ToString();
    }

    private static bool TryReadTransform(
        ReadOnlySpan<char> value,
        out ReadOnlySpan<char> content,
        out int consumedLength)
    {
        content = default;
        consumedLength = 0;
        if (value.IsEmpty || char.ToLowerInvariant(value[0]) != 't')
        {
            return false;
        }

        var open = 1;
        while (open < value.Length && char.IsWhiteSpace(value[open]))
        {
            open++;
        }

        if (open >= value.Length || value[open] != '(')
        {
            return false;
        }

        var depth = 1;
        for (var index = open + 1; index < value.Length; index++)
        {
            if (value[index] == '(')
            {
                depth++;
            }
            else if (value[index] == ')' && --depth == 0)
            {
                content = value[(open + 1)..index];
                consumedLength = index + 1;
                return true;
            }
        }

        return false;
    }

    private static RenderState ApplyTag(
        string tag,
        string value,
        RenderState current,
        StyleFont baseStyle,
        IReadOnlyDictionary<string, StyleFont> styles) => tag switch
        {
            "fn" => current with
            {
                FamilyName = value.Length == 0 || value == "0"
                    ? current.ResetStyle.FamilyName
                    : value
            },
            "b" => current with { Weight = ReadInlineWeight(value, current.ResetStyle.Weight) },
            "i" => current with { Italic = ReadInlineItalic(value, current.ResetStyle.Italic) },
            "p" => current with { DrawingScale = ReadDrawingScale(value) },
            "r" => RenderState.FromStyle(ResolveResetStyle(value, baseStyle, styles)),
            _ => current
        };

    private static StyleFont ResolveResetStyle(
        string styleName,
        StyleFont baseStyle,
        IReadOnlyDictionary<string, StyleFont> styles)
    {
        var normalized = styleName.Trim();
        return normalized.Length > 0 && styles.TryGetValue(normalized, out var style)
            ? style
            : baseStyle;
    }

    private static int ReadInlineWeight(string value, int fallback)
    {
        if (!TryReadLeadingInteger(value, out var number))
        {
            return fallback;
        }

        return number switch
        {
            0 => 400,
            1 => 700,
            >= 100 => number,
            _ => fallback
        };
    }

    private static bool ReadInlineItalic(string value, bool fallback)
    {
        if (!TryReadLeadingInteger(value, out var number) || number is not (0 or 1))
        {
            return fallback;
        }

        return number == 1;
    }

    private static int ReadDrawingScale(string value) =>
        TryReadLeadingInteger(value, out var number) ? Math.Max(0, number) : 0;

    private static bool TryReadLeadingInteger(string value, out int number)
    {
        number = 0;
        value = value.TrimStart();
        var length = 0;
        if (length < value.Length && value[length] is '+' or '-')
        {
            length++;
        }

        var digitStart = length;
        while (length < value.Length && char.IsDigit(value[length]))
        {
            length++;
        }

        return length > digitStart && int.TryParse(value.AsSpan(0, length), out number);
    }

    private static int ReadStyleWeight(IReadOnlyList<string> values, int index) =>
        TryReadLeadingInteger(GetValue(values, index), out var value) && value != 0 ? 700 : 400;

    private static bool ReadStyleItalic(IReadOnlyList<string> values, int index) =>
        TryReadLeadingInteger(GetValue(values, index), out var value) && value != 0;

    private static int FindField(IReadOnlyList<string> fields, string name, int fallback)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (fields[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return fallback;
    }

    private static string GetValue(IReadOnlyList<string> values, int index) =>
        index >= 0 && index < values.Count ? values[index].Trim() : string.Empty;

    private static bool TryReadSection(string value, out string section)
    {
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            section = value[1..^1].Trim();
            return true;
        }

        section = string.Empty;
        return false;
    }

    private sealed record StyleFont(string FamilyName, int Weight, bool Italic);

    private sealed record RenderState(
        string FamilyName,
        int Weight,
        bool Italic,
        int DrawingScale,
        StyleFont ResetStyle)
    {
        public static RenderState FromStyle(StyleFont style) =>
            new(style.FamilyName, style.Weight, style.Italic, 0, style);
    }

    private sealed class AssFontRequirementComparer : IEqualityComparer<AssFontRequirement>
    {
        public static AssFontRequirementComparer Instance { get; } = new();

        public bool Equals(AssFontRequirement? x, AssFontRequirement? y) =>
            ReferenceEquals(x, y)
            || x is not null
            && y is not null
            && x.Weight == y.Weight
            && x.Italic == y.Italic
            && x.FamilyName.Equals(y.FamilyName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(AssFontRequirement value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.FamilyName),
            value.Weight,
            value.Italic);
    }
}
