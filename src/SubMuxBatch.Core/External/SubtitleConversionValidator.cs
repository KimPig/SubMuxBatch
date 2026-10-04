using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.External;

/// <summary>
/// Validates the observable result of an SRT-to-ASS conversion. The comparison is
/// intentionally limited to cue timing, visible text and the FONT attributes that
/// SubMux canonicalizes before conversion; it does not try to emulate a subtitle renderer.
/// </summary>
public static partial class SubtitleConversionValidator
{
    public static void ValidateAssOptimization(string originalAss, string optimizedAss)
    {
        ArgumentNullException.ThrowIfNull(originalAss);
        ArgumentNullException.ThrowIfNull(optimizedAss);

        var originalCues = ParseAss(originalAss);
        var optimizedCues = ParseAss(optimizedAss);
        var originalDefault = ParseDefaultStyle(originalAss);
        var optimizedDefault = ParseDefaultStyle(optimizedAss);
        if (originalCues.Count != optimizedCues.Count)
        {
            throw new InvalidDataException(CoreText.Get("Subtitle_AssOptimizationMismatch", 0));
        }

        for (var index = 0; index < originalCues.Count; index++)
        {
            var original = originalCues[index];
            var optimized = optimizedCues[index];
            if (original.StartMilliseconds != optimized.StartMilliseconds
                || original.EndMilliseconds != optimized.EndMilliseconds
                || !string.Equals(
                    AssOverrideBlockRegex().Replace(original.Text, string.Empty),
                    AssOverrideBlockRegex().Replace(optimized.Text, string.Empty),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(CoreText.Get("Subtitle_AssOptimizationMismatch", index + 1));
            }

            var expected = ParseAssStyledCharacters(original.Text, originalDefault);
            var actual = ParseAssStyledCharacters(optimized.Text, optimizedDefault);
            if (expected.Count != actual.Count)
            {
                throw new InvalidDataException(CoreText.Get("Subtitle_AssOptimizationMismatch", index + 1));
            }
            for (var characterIndex = 0; characterIndex < expected.Count; characterIndex++)
            {
                var left = expected[characterIndex];
                var right = actual[characterIndex];
                if (left.Value != right.Value
                    || !FontNamesEqual(left.Style.FontName, right.Style.FontName)
                    || !string.Equals(left.Style.Colour, right.Style.Colour, StringComparison.OrdinalIgnoreCase)
                    || !FontSizesEqual(left.Style.FontSize, right.Style.FontSize))
                {
                    throw new InvalidDataException(CoreText.Get("Subtitle_AssOptimizationMismatch", index + 1));
                }
            }
        }
    }

    public static void ValidateSrtToAss(string preparedSrt, string convertedAss)
    {
        ArgumentNullException.ThrowIfNull(preparedSrt);
        ArgumentNullException.ThrowIfNull(convertedAss);

        var srtCues = ParseSrt(preparedSrt);
        var assCues = ParseAss(convertedAss);
        var defaultStyle = ParseDefaultStyle(convertedAss);
        if (srtCues.Count != assCues.Count)
        {
            throw new InvalidDataException(CoreText.Get(
                "Subtitle_AssValidationCueCount",
                srtCues.Count,
                assCues.Count));
        }

        for (var index = 0; index < srtCues.Count; index++)
        {
            var source = srtCues[index];
            var target = assCues[index];
            var cueNumber = index + 1;
            if (Math.Abs(source.StartMilliseconds - target.StartMilliseconds) > 15
                || Math.Abs(source.EndMilliseconds - target.EndMilliseconds) > 15)
            {
                throw new InvalidDataException(CoreText.Get(
                    "Subtitle_AssValidationTiming",
                    cueNumber,
                    FormatTimestamp(source.StartMilliseconds),
                    FormatTimestamp(source.EndMilliseconds)));
            }

            if (LiteralFormattingTagRegex().IsMatch(target.Text))
            {
                throw new InvalidDataException(CoreText.Get(
                    "Subtitle_AssValidationTagLeak",
                    cueNumber,
                    FormatTimestamp(source.StartMilliseconds)));
            }

            var expectedText = NormalizeVisibleText(source.Text, isAss: false);
            var actualText = NormalizeVisibleText(target.Text, isAss: true);
            if (!string.Equals(expectedText, actualText, StringComparison.Ordinal))
            {
                throw new InvalidDataException(CoreText.Get(
                    "Subtitle_AssValidationText",
                    cueNumber,
                    FormatTimestamp(source.StartMilliseconds)));
            }

            ValidateFontFormatting(source, target, defaultStyle, cueNumber);
        }
    }

    private static void ValidateFontFormatting(
        SrtCue source,
        AssCue target,
        InlineStyleState defaultStyle,
        int cueNumber)
    {
        var expected = ParseSrtStyledCharacters(source.Text, defaultStyle);
        var actual = ParseAssStyledCharacters(target.Text, defaultStyle);
        if (expected.Count != actual.Count)
        {
            throw new InvalidDataException(CoreText.Get(
                "Subtitle_AssValidationText",
                cueNumber,
                FormatTimestamp(source.StartMilliseconds)));
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var expectedCharacter = expected[index];
            var actualCharacter = actual[index];
            if (expectedCharacter.Value != actualCharacter.Value)
            {
                throw new InvalidDataException(CoreText.Get(
                    "Subtitle_AssValidationText",
                    cueNumber,
                    FormatTimestamp(source.StartMilliseconds)));
            }

            if (!FontNamesEqual(expectedCharacter.Style.FontName, actualCharacter.Style.FontName))
            {
                throw new InvalidDataException(CoreText.Get(
                    "Subtitle_AssValidationFont",
                    cueNumber,
                    FormatTimestamp(source.StartMilliseconds),
                    expectedCharacter.Style.FontName ?? "Default"));
            }

            if (expectedCharacter.Style.CompareColour
                && !string.Equals(
                    expectedCharacter.Style.Colour,
                    actualCharacter.Style.Colour,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(CoreText.Get(
                    "Subtitle_AssValidationColor",
                    cueNumber,
                    FormatTimestamp(source.StartMilliseconds),
                    expectedCharacter.Style.Colour ?? "Default"));
            }

            if (!FontSizesEqual(expectedCharacter.Style.FontSize, actualCharacter.Style.FontSize))
            {
                throw new InvalidDataException(CoreText.Get(
                    "Subtitle_AssValidationFontSize",
                    cueNumber,
                    FormatTimestamp(source.StartMilliseconds),
                    expectedCharacter.Style.FontSize?.ToString("0.###", CultureInfo.InvariantCulture)
                    ?? "Default"));
            }
        }
    }

    private static List<StyledCharacter> ParseSrtStyledCharacters(string text, InlineStyleState defaultStyle)
    {
        var characters = new List<StyledCharacter>();
        var states = new Stack<InlineStyleState>();
        states.Push(defaultStyle);
        var position = 0;
        foreach (Match tag in HtmlTokenRegex().Matches(text))
        {
            AppendCharacters(characters, text[position..tag.Index], states.Peek(), isAss: false);
            var value = tag.Value;
            if (HtmlBreakRegex().IsMatch(value))
            {
                AppendCharacters(characters, "\n", states.Peek(), isAss: false);
            }
            else if (value.StartsWith("</font", StringComparison.OrdinalIgnoreCase))
            {
                if (states.Count > 1) states.Pop();
            }
            else if (value.StartsWith("<font", StringComparison.OrdinalIgnoreCase)
                     && !value.TrimEnd().EndsWith("/>", StringComparison.Ordinal))
            {
                var next = states.Peek();
                var match = CanonicalFontTagRegex().Match(value);
                foreach (Match attribute in CanonicalFontAttributeRegex().Matches(match.Groups["attributes"].Value))
                {
                    var name = attribute.Groups["name"].Value;
                    var attributeValue = WebUtility.HtmlDecode(attribute.Groups["value"].Value).Trim();
                    if (name.Equals("face", StringComparison.OrdinalIgnoreCase))
                    {
                        next = next with { FontName = NormalizeFontName(attributeValue) };
                    }
                    else if (name.Equals("color", StringComparison.OrdinalIgnoreCase))
                    {
                        var resolution = LibSeFontColourResolver.Resolve(attributeValue);
                        next = resolution.Recognized
                            ? next with
                            {
                                Colour = resolution.CanonicalRgb,
                                CompareColour = true
                            }
                            : next with { CompareColour = false };
                    }
                    else if (name.Equals("size", StringComparison.OrdinalIgnoreCase)
                             && double.TryParse(
                                 attributeValue,
                                 NumberStyles.Float,
                                 CultureInfo.InvariantCulture,
                                 out var size))
                    {
                        next = next with { FontSize = size };
                    }
                }

                states.Push(next);
            }

            position = tag.Index + tag.Length;
        }

        AppendCharacters(characters, text[position..], states.Peek(), isAss: false);
        return characters;
    }

    private static List<StyledCharacter> ParseAssStyledCharacters(string text, InlineStyleState defaultStyle)
    {
        var characters = new List<StyledCharacter>();
        var current = defaultStyle;
        var position = 0;
        foreach (Match block in AssOverrideBlockRegex().Matches(text))
        {
            AppendCharacters(characters, text[position..block.Index], current, isAss: true);
            foreach (Match tag in AssStateTagRegex().Matches(block.Value))
            {
                var name = tag.Groups["name"].Value;
                var value = tag.Groups["value"].Value.Trim();
                if (name.Equals("r", StringComparison.OrdinalIgnoreCase))
                {
                    current = defaultStyle;
                }
                else if (name.Equals("fn", StringComparison.OrdinalIgnoreCase))
                {
                    current = current with
                    {
                        FontName = value.Length == 0 ? defaultStyle.FontName : NormalizeFontName(value)
                    };
                }
                else if (name.Equals("fs", StringComparison.OrdinalIgnoreCase))
                {
                    current = current with
                    {
                        FontSize = value.Length == 0
                            ? defaultStyle.FontSize
                            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
                                ? size
                                : current.FontSize
                    };
                }
                else if (name.Equals("c", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("1c", StringComparison.OrdinalIgnoreCase))
                {
                    current = current with
                    {
                        Colour = value.Length == 0 ? defaultStyle.Colour : NormalizeAssColour(value),
                        CompareColour = true
                    };
                }
            }

            position = block.Index + block.Length;
        }

        AppendCharacters(characters, text[position..], current, isAss: true);
        return characters;
    }

    private static void AppendCharacters(
        ICollection<StyledCharacter> output,
        string text,
        InlineStyleState style,
        bool isAss)
    {
        var visible = isAss
            ? text.Replace("\\N", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("\\h", " ", StringComparison.OrdinalIgnoreCase)
            : AssOverrideBlockRegex().Replace(text, string.Empty);
        visible = WebUtility.HtmlDecode(visible)
            .Replace('\u00A0', ' ')
            .Normalize(NormalizationForm.FormC);
        foreach (var rune in visible.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune))
            {
                output.Add(new StyledCharacter(rune.Value, style));
            }
        }
    }

    private static InlineStyleState ParseDefaultStyle(string ass)
    {
        foreach (var line in NormalizeLineEndings(ass).Split('\n'))
        {
            if (AssStyleDefinition.TryParse(line.Trim(), out var definition)
                && string.Equals(definition!.Name, "Default", StringComparison.Ordinal))
            {
                return new InlineStyleState(
                    NormalizeFontName(definition.FontName),
                    NormalizeAssColour(definition.PrimaryColour),
                    definition.FontSize,
                    true);
            }
        }

        return new InlineStyleState(null, null, null, true);
    }

    private static string? NormalizeAssColour(string value)
    {
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length < 6) return null;
        var bgr = hex[^6..];
        return $"#{bgr[4..6]}{bgr[2..4]}{bgr[0..2]}".ToUpperInvariant();
    }

    private static string NormalizeFontName(string value) =>
        value.Trim().Normalize(NormalizationForm.FormKC);

    private static bool FontNamesEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool FontSizesEqual(double? left, double? right) =>
        left is null && right is null
        || left is not null && right is not null && Math.Abs(left.Value - right.Value) < 0.01;

    private static List<SrtCue> ParseSrt(string text)
    {
        var cues = new List<SrtCue>();
        var lines = NormalizeLineEndings(text).Split('\n');
        for (var timeIndex = 0; timeIndex < lines.Length; timeIndex++)
        {
            if (!SrtTimestampRegex().IsMatch(lines[timeIndex])) continue;
            var time = SrtTimestampRegex().Match(lines[timeIndex]);
            var nextTimeIndex = timeIndex + 1;
            while (nextTimeIndex < lines.Length && !SrtTimestampRegex().IsMatch(lines[nextTimeIndex]))
            {
                nextTimeIndex++;
            }
            var textEndExclusive = nextTimeIndex;
            if (nextTimeIndex < lines.Length
                && nextTimeIndex > timeIndex + 1
                && int.TryParse(lines[nextTimeIndex - 1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                textEndExclusive--;
            }
            while (textEndExclusive > timeIndex + 1
                   && string.IsNullOrWhiteSpace(lines[textEndExclusive - 1]))
            {
                textEndExclusive--;
            }
            cues.Add(new SrtCue(
                ParseSrtTime(time, "sh", "sm", "ss", "sms"),
                ParseSrtTime(time, "eh", "em", "es", "ems"),
                string.Join('\n', lines[(timeIndex + 1)..textEndExclusive])));
            timeIndex = nextTimeIndex - 1;
        }

        return cues;
    }

    private static List<AssCue> ParseAss(string text)
    {
        var cues = new List<AssCue>();
        foreach (var line in NormalizeLineEndings(text).Split('\n'))
        {
            if (!line.TrimStart().StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var colon = line.IndexOf(':');
            var fields = SplitDialogueFields(line[(colon + 1)..].TrimStart());
            if (fields is null || !TryParseAssTime(fields[1], out var start) || !TryParseAssTime(fields[2], out var end))
            {
                throw new InvalidDataException(CoreText.Get("Subtitle_AssValidationMalformedDialogue"));
            }

            cues.Add(new AssCue(start, end, fields[9]));
        }

        return cues;
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

    private static string NormalizeVisibleText(string text, bool isAss)
    {
        var visible = isAss
            ? RemoveAssOverridesAndDrawings(text)
                .Replace("\\N", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("\\h", " ", StringComparison.OrdinalIgnoreCase)
            : AssOverrideBlockRegex().Replace(HtmlBreakRegex().Replace(text, "\n"), string.Empty);
        if (!isAss)
        {
            visible = HtmlTagRegex().Replace(visible, string.Empty);
        }

        visible = WebUtility.HtmlDecode(visible)
            .Replace('\u00A0', ' ')
            .Normalize(NormalizationForm.FormC);
        return string.Join('\n', NormalizeLineEndings(visible).Split('\n').Select(static line => line.Trim())).Trim();
    }

    private static string RemoveAssOverridesAndDrawings(string text)
    {
        var output = new StringBuilder(text.Length);
        var drawingScale = 0;
        var position = 0;
        foreach (Match block in AssOverrideBlockRegex().Matches(text))
        {
            if (drawingScale == 0)
            {
                output.Append(text, position, block.Index - position);
            }
            foreach (Match drawingTag in AssDrawingModeRegex().Matches(block.Value))
            {
                drawingScale = int.TryParse(
                    drawingTag.Groups["scale"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? Math.Max(0, parsed)
                    : drawingScale;
            }
            position = block.Index + block.Length;
        }
        if (drawingScale == 0)
        {
            output.Append(text, position, text.Length - position);
        }
        return output.ToString();
    }

    private static long ParseSrtTime(Match match, string hours, string minutes, string seconds, string milliseconds) =>
        ((long)Parse(match.Groups[hours].Value) * 3600
         + Parse(match.Groups[minutes].Value) * 60
         + Parse(match.Groups[seconds].Value)) * 1000
        + Parse(match.Groups[milliseconds].Value);

    private static bool TryParseAssTime(string value, out long milliseconds)
    {
        var match = AssTimestampRegex().Match(value.Trim());
        if (!match.Success)
        {
            milliseconds = 0;
            return false;
        }

        var fraction = match.Groups["fraction"].Value.PadRight(3, '0');
        milliseconds = ((long)Parse(match.Groups["h"].Value) * 3600
                        + Parse(match.Groups["m"].Value) * 60
                        + Parse(match.Groups["s"].Value)) * 1000
                       + Parse(fraction);
        return true;
    }

    private static int Parse(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string FormatTimestamp(long milliseconds) =>
        $"{milliseconds / 3_600_000:00}:{milliseconds / 60_000 % 60:00}:{milliseconds / 1_000 % 60:00}.{milliseconds % 1_000:000}";

    private sealed record SrtCue(long StartMilliseconds, long EndMilliseconds, string Text);
    private sealed record AssCue(long StartMilliseconds, long EndMilliseconds, string Text);
    private sealed record InlineStyleState(
        string? FontName,
        string? Colour,
        double? FontSize,
        bool CompareColour);
    private sealed record StyledCharacter(int Value, InlineStyleState Style);

    [GeneratedRegex(@"(?<sh>\d{1,3}):(?<sm>\d{2}):(?<ss>\d{2})[,.](?<sms>\d{3})\s*-->\s*(?<eh>\d{1,3}):(?<em>\d{2}):(?<es>\d{2})[,.](?<ems>\d{3})")]
    private static partial Regex SrtTimestampRegex();

    [GeneratedRegex(@"^(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})[.](?<fraction>\d{1,3})$")]
    private static partial Regex AssTimestampRegex();

    [GeneratedRegex(@"<font\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalFontTagRegex();

    [GeneratedRegex("(?<name>color|face|size)=\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalFontAttributeRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTokenRegex();

    [GeneratedRegex(@"\\(?<name>fn|fs|1c|c|r)(?<value>[^\\}]*)", RegexOptions.IgnoreCase)]
    private static partial Regex AssStateTagRegex();

    [GeneratedRegex(@"</?(?:font|ruby|rb|rt|rp)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LiteralFormattingTagRegex();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex AssOverrideBlockRegex();

    [GeneratedRegex(@"\\p(?<scale>-?\d+)(?=\\|\}|\s)", RegexOptions.IgnoreCase)]
    private static partial Regex AssDrawingModeRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
