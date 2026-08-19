using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SubMuxBatch.Core.External;

public sealed record NegativeSubtitleTimestampAdjustment(
    int LineNumber,
    string OriginalRange,
    string AdjustedRange);

public static partial class SubtitleCompatibilityNormalizer
{
    public static async Task<IReadOnlyList<NegativeSubtitleTimestampAdjustment>> NormalizeNegativeSrtTimestampsAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var text = DecodeSubtitle(bytes);
        var adjustments = new List<NegativeSubtitleTimestampAdjustment>();
        var currentLine = 1;
        var scannedIndex = 0;
        var normalized = SrtTimestampLineRegex().Replace(text, match =>
        {
            AdvanceLineNumber(text, match.Index, ref currentLine, ref scannedIndex);
            var originalStart = match.Groups["start"].Value;
            var originalEnd = match.Groups["end"].Value;
            if (!TryParseSrtTimestamp(originalStart, out var startMilliseconds)
                || !TryParseSrtTimestamp(originalEnd, out var endMilliseconds)
                || (startMilliseconds >= 0 && endMilliseconds >= 0))
            {
                return match.Value;
            }

            var adjustedStart = FormatSrtTimestamp(Math.Max(0, startMilliseconds));
            var adjustedEnd = FormatSrtTimestamp(Math.Max(0, endMilliseconds));
            var originalRange = $"{originalStart} --> {originalEnd}";
            var adjustedRange = $"{adjustedStart} --> {adjustedEnd}";
            adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                currentLine,
                originalRange,
                adjustedRange));

            return $"{match.Groups["indent"].Value}{adjustedRange}{match.Groups["suffix"].Value}{match.Groups["cr"].Value}";
        });

        await File.WriteAllTextAsync(
            outputPath,
            normalized,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        return adjustments;
    }

    public static async Task<IReadOnlyList<NegativeSubtitleTimestampAdjustment>> NormalizeNegativeAssTimestampsAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var text = DecodeSubtitle(bytes);
        var adjustments = new List<NegativeSubtitleTimestampAdjustment>();
        var currentLine = 1;
        var scannedIndex = 0;
        var normalized = AssDialogueLineRegex().Replace(text, match =>
        {
            AdvanceLineNumber(text, match.Index, ref currentLine, ref scannedIndex);
            var originalStart = match.Groups["start"].Value.Trim();
            var originalEnd = match.Groups["end"].Value.Trim();
            if (!TryParseAssTimestamp(originalStart, out var startMilliseconds)
                || !TryParseAssTimestamp(originalEnd, out var endMilliseconds)
                || (startMilliseconds >= 0 && endMilliseconds >= 0))
            {
                return match.Value;
            }

            var adjustedStart = startMilliseconds < 0 ? "0:00:00.00" : originalStart;
            var adjustedEnd = endMilliseconds < 0 ? "0:00:00.00" : originalEnd;
            var originalRange = $"{originalStart} --> {originalEnd}";
            var adjustedRange = $"{adjustedStart} --> {adjustedEnd}";
            adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                currentLine,
                originalRange,
                adjustedRange));

            return $"{match.Groups["prefix"].Value}{adjustedStart},{adjustedEnd}{match.Groups["suffix"].Value}{match.Groups["cr"].Value}";
        });

        await File.WriteAllTextAsync(
            outputPath,
            normalized,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        return adjustments;
    }

    public static async Task<IReadOnlyList<NegativeSubtitleTimestampAdjustment>> NormalizeNegativeSmiTimestampsAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var text = DecodeSubtitle(bytes);
        var adjustments = new List<NegativeSubtitleTimestampAdjustment>();
        var currentLine = 1;
        var scannedIndex = 0;
        var normalized = SmiSyncTagRegex().Replace(text, match =>
        {
            AdvanceLineNumber(text, match.Index, ref currentLine, ref scannedIndex);
            var originalValue = match.Groups["value"].Value;
            adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                currentLine,
                $"Start={originalValue} ms",
                "Start=0 ms"));
            return $"{match.Groups["prefix"].Value}0{match.Groups["suffix"].Value}";
        });

        await File.WriteAllTextAsync(
            outputPath,
            normalized,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        return adjustments;
    }

    public static async Task PrepareSrtForAssAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var text = DecodeSubtitle(bytes);
        var normalized = FlattenRuby(text);
        normalized = NormalizeSupportedHtmlTags(normalized);
        await File.WriteAllTextAsync(
            outputPath,
            normalized,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static string DecodeSubtitle(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }

    private static bool TryParseSrtTimestamp(string value, out long milliseconds)
    {
        milliseconds = 0;
        var negative = false;
        var timestamp = value;
        if (timestamp.StartsWith("-", StringComparison.Ordinal))
        {
            negative = true;
            timestamp = timestamp[1..];
        }

        var components = timestamp.Split(':');
        if (components.Length != 3)
        {
            return false;
        }

        if (components[2].StartsWith("-", StringComparison.Ordinal))
        {
            if (negative)
            {
                return false;
            }

            negative = true;
            components[2] = components[2][1..];
        }

        var secondsAndMilliseconds = components[2].Split([',', '.']);
        if (secondsAndMilliseconds.Length != 2
            || !long.TryParse(components[0], out var hours)
            || !long.TryParse(components[1], out var minutes)
            || !long.TryParse(secondsAndMilliseconds[0], out var seconds)
            || !long.TryParse(secondsAndMilliseconds[1], out var parsedMilliseconds)
            || minutes is < 0 or > 59
            || seconds is < 0 or > 59
            || parsedMilliseconds is < 0 or > 999)
        {
            return false;
        }

        try
        {
            milliseconds = checked((((hours * 60) + minutes) * 60 + seconds) * 1000 + parsedMilliseconds);
            if (negative)
            {
                milliseconds = -milliseconds;
            }

            return true;
        }
        catch (OverflowException)
        {
            milliseconds = 0;
            return false;
        }
    }

    private static bool TryParseAssTimestamp(string value, out long milliseconds)
    {
        milliseconds = 0;
        var timestamp = value.Replace(',', '.');
        var negative = false;
        if (timestamp.StartsWith("-", StringComparison.Ordinal))
        {
            negative = true;
            timestamp = timestamp[1..];
        }

        var components = timestamp.Split(':');
        if (components.Length != 3)
        {
            return false;
        }

        if (components[2].StartsWith("-", StringComparison.Ordinal))
        {
            if (negative)
            {
                return false;
            }

            negative = true;
            components[2] = components[2][1..];
        }

        var secondsAndFraction = components[2].Split('.');
        if (secondsAndFraction.Length != 2
            || secondsAndFraction[1].Length is < 1 or > 3
            || !long.TryParse(components[0], out var hours)
            || !long.TryParse(components[1], out var minutes)
            || !long.TryParse(secondsAndFraction[0], out var seconds)
            || !long.TryParse(secondsAndFraction[1].PadRight(3, '0'), out var parsedMilliseconds)
            || minutes is < 0 or > 59
            || seconds is < 0 or > 59)
        {
            return false;
        }

        try
        {
            milliseconds = checked((((hours * 60) + minutes) * 60 + seconds) * 1000 + parsedMilliseconds);
            if (negative)
            {
                milliseconds = -milliseconds;
            }

            return true;
        }
        catch (OverflowException)
        {
            milliseconds = 0;
            return false;
        }
    }

    private static string FormatSrtTimestamp(long milliseconds)
    {
        var hours = milliseconds / 3_600_000;
        var minutes = milliseconds / 60_000 % 60;
        var seconds = milliseconds / 1_000 % 60;
        var remainder = milliseconds % 1_000;
        return $"{hours:00}:{minutes:00}:{seconds:00},{remainder:000}";
    }

    private static void AdvanceLineNumber(
        string text,
        int targetIndex,
        ref int currentLine,
        ref int scannedIndex)
    {
        for (var index = scannedIndex; index < targetIndex; index++)
        {
            if (text[index] == '\n')
            {
                currentLine++;
            }
        }

        scannedIndex = targetIndex;
    }

    private static string FlattenRuby(string text)
    {
        var result = RubyBlockRegex().Replace(text, static match =>
        {
            var content = match.Groups["content"].Value;
            var readings = RubyTextRegex().Matches(content)
                .Select(static reading => StripTags(reading.Groups["text"].Value).Trim())
                .Where(static reading => reading.Length > 0)
                .ToArray();
            var baseText = RubyTextRegex().Replace(content, string.Empty);
            baseText = RubyParenthesisRegex().Replace(baseText, string.Empty);
            baseText = RubyBaseTagRegex().Replace(baseText, string.Empty);
            return readings.Length == 0 ? baseText : $"{baseText}({string.Join("/", readings)})";
        });

        // Avoid literal tag leakage for malformed or unclosed ruby fragments.
        return OrphanRubyTagRegex().Replace(result, string.Empty);
    }

    private static string NormalizeSupportedHtmlTags(string text)
    {
        var normalized = SupportedTagNameRegex().Replace(text, static match =>
            $"<{match.Groups["slash"].Value}{match.Groups["name"].Value.ToLowerInvariant()}");

        return FontTagRegex().Replace(normalized, static tag =>
            FontAttributeNameRegex().Replace(tag.Value, static attribute =>
                $"{attribute.Groups["name"].Value.ToLowerInvariant()}="));
    }

    private static string StripTags(string text) =>
        WebUtility.HtmlDecode(AnyHtmlTagRegex().Replace(text, string.Empty));

    [GeneratedRegex(@"<ruby\b[^>]*>(?<content>.*?)</ruby\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RubyBlockRegex();

    [GeneratedRegex(@"<rt\b[^>]*>(?<text>.*?)</rt\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RubyTextRegex();

    [GeneratedRegex(@"<rp\b[^>]*>.*?</rp\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RubyParenthesisRegex();

    [GeneratedRegex(@"</?rb\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex RubyBaseTagRegex();

    [GeneratedRegex(@"</?(?:ruby|rt|rp|rb)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex OrphanRubyTagRegex();

    [GeneratedRegex(@"<\s*(?<slash>/?)\s*(?<name>font|b|i|u|s|br)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SupportedTagNameRegex();

    [GeneratedRegex(@"<font\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FontTagRegex();

    [GeneratedRegex(@"(?<name>color|face|size)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex FontAttributeNameRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex AnyHtmlTagRegex();

    [GeneratedRegex(
        @"^(?<indent>[ \t]*)(?<start>(?:-?\d+:\d{2}:\d{2}|\d+:\d{2}:-\d{2})[,.]\d{3})[ \t]*-->[ \t]*(?<end>(?:-?\d+:\d{2}:\d{2}|\d+:\d{2}:-\d{2})[,.]\d{3})(?<suffix>[^\r\n]*)(?<cr>\r?)$",
        RegexOptions.Multiline)]
    private static partial Regex SrtTimestampLineRegex();

    [GeneratedRegex(
        @"^(?<prefix>[ \t]*Dialogue[ \t]*:[^,\r\n]*,)(?<start>[^,\r\n]*),(?<end>[^,\r\n]*)(?<suffix>,[^\r\n]*)(?<cr>\r?)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AssDialogueLineRegex();

    [GeneratedRegex(
        @"(?<prefix><sync\b[^>]*\bstart\s*=\s*[""']?)(?<value>-\d+)(?<suffix>[""']?[^>]*>)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SmiSyncTagRegex();
}
