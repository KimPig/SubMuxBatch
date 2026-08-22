using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SubMuxBatch.Core.External;

public enum SubtitleTimestampAdjustmentKind
{
    Adjusted,
    RemovedBeforeVideoStart,
    RemovedInvalidRange,
    RemovedInvalidTimestamp
}

public sealed record NegativeSubtitleTimestampAdjustment(
    int LineNumber,
    string OriginalRange,
    string AdjustedRange,
    SubtitleTimestampAdjustmentKind Kind = SubtitleTimestampAdjustmentKind.Adjusted)
{
    public bool Removed => Kind != SubtitleTimestampAdjustmentKind.Adjusted;
}

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
        var lines = NormalizeLineEndings(text).Split('\n');
        var candidates = new List<SrtCueCandidate>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var match = SrtTimestampLineRegex().Match(lines[lineIndex]);
            if (!match.Success)
            {
                continue;
            }

            var originalStart = match.Groups["start"].Value;
            var originalEnd = match.Groups["end"].Value;
            var startParsed = TryParseSrtTimestamp(
                originalStart,
                out var startMilliseconds,
                out var startHasNegativeComponent);
            var endParsed = TryParseSrtTimestamp(
                originalEnd,
                out var endMilliseconds,
                out var endHasNegativeComponent);
            var parsed = startParsed && endParsed;
            var cueStartLine = lineIndex > 0 && SrtSequenceNumberRegex().IsMatch(lines[lineIndex - 1])
                ? lineIndex - 1
                : lineIndex;
            candidates.Add(new SrtCueCandidate(
                cueStartLine,
                lineIndex,
                lineIndex + 1,
                originalStart,
                originalEnd,
                match.Groups["suffix"].Value,
                parsed,
                startMilliseconds,
                endMilliseconds,
                startHasNegativeComponent || endHasNegativeComponent));
        }

        var output = new StringBuilder();
        var outputSequence = 1;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[candidateIndex];
            var bodyEndLine = candidateIndex + 1 < candidates.Count
                ? candidates[candidateIndex + 1].CueStartLine
                : lines.Length;
            while (bodyEndLine > candidate.BodyStartLine
                   && string.IsNullOrWhiteSpace(lines[bodyEndLine - 1]))
            {
                bodyEndLine--;
            }

            var originalRange = $"{candidate.OriginalStart} --> {candidate.OriginalEnd}";
            if (!candidate.Parsed)
            {
                adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                    candidate.TimestampLine + 1,
                    originalRange,
                    string.Empty,
                    SubtitleTimestampAdjustmentKind.RemovedInvalidTimestamp));
                continue;
            }

            if (candidate.EndMilliseconds <= 0)
            {
                adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                    candidate.TimestampLine + 1,
                    originalRange,
                    string.Empty,
                    SubtitleTimestampAdjustmentKind.RemovedBeforeVideoStart));
                continue;
            }

            var adjustedStartMilliseconds = Math.Max(0, candidate.StartMilliseconds);
            if (candidate.EndMilliseconds <= adjustedStartMilliseconds)
            {
                adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                    candidate.TimestampLine + 1,
                    originalRange,
                    string.Empty,
                    SubtitleTimestampAdjustmentKind.RemovedInvalidRange));
                continue;
            }

            var adjustedStart = FormatSrtTimestamp(adjustedStartMilliseconds);
            var adjustedEnd = FormatSrtTimestamp(candidate.EndMilliseconds);
            var adjustedRange = $"{adjustedStart} --> {adjustedEnd}";
            if (candidate.HasNegativeComponent)
            {
                adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                    candidate.TimestampLine + 1,
                    originalRange,
                    adjustedRange));
            }

            output.Append(outputSequence++).Append("\r\n");
            output.Append(adjustedRange).Append(candidate.Suffix).Append("\r\n");
            for (var bodyLine = candidate.BodyStartLine; bodyLine < bodyEndLine; bodyLine++)
            {
                output.Append(lines[bodyLine]).Append("\r\n");
            }

            output.Append("\r\n");
        }

        await File.WriteAllTextAsync(
            outputPath,
            output.ToString(),
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
            var startParsed = TryParseAssTimestamp(
                originalStart,
                out var startMilliseconds,
                out var startHasNegativeComponent);
            var endParsed = TryParseAssTimestamp(
                originalEnd,
                out var endMilliseconds,
                out var endHasNegativeComponent);
            var originalRange = $"{originalStart} --> {originalEnd}";
            if (!startParsed || !endParsed)
            {
                adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                    currentLine,
                    originalRange,
                    string.Empty,
                    SubtitleTimestampAdjustmentKind.RemovedInvalidTimestamp));
                return match.Groups["cr"].Value;
            }

            if (endMilliseconds <= 0)
            {
                adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                    currentLine,
                    originalRange,
                    string.Empty,
                    SubtitleTimestampAdjustmentKind.RemovedBeforeVideoStart));
                return match.Groups["cr"].Value;
            }

            var adjustedStartMilliseconds = Math.Max(0, startMilliseconds);
            if (endMilliseconds <= adjustedStartMilliseconds)
            {
                adjustments.Add(new NegativeSubtitleTimestampAdjustment(
                    currentLine,
                    originalRange,
                    string.Empty,
                    SubtitleTimestampAdjustmentKind.RemovedInvalidRange));
                return match.Groups["cr"].Value;
            }

            if (!startHasNegativeComponent && !endHasNegativeComponent)
            {
                return match.Value;
            }

            var adjustedStart = startMilliseconds < 0 ? "0:00:00.00" : originalStart;
            var adjustedEnd = originalEnd;
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

    private static bool TryParseSrtTimestamp(
        string value,
        out long milliseconds,
        out bool hasNegativeComponent)
    {
        milliseconds = 0;
        hasNegativeComponent = false;
        var overallNegative = false;
        var timestamp = value.Trim();
        if (timestamp.StartsWith("-", StringComparison.Ordinal))
        {
            overallNegative = true;
            hasNegativeComponent = true;
            timestamp = timestamp[1..];
        }
        else if (timestamp.StartsWith("+", StringComparison.Ordinal))
        {
            timestamp = timestamp[1..];
        }

        var components = timestamp.Split(':');
        if (components.Length != 3)
        {
            return false;
        }

        var secondsAndMilliseconds = components[2].Split([',', '.']);
        if (secondsAndMilliseconds.Length is < 1 or > 2
            || !long.TryParse(components[0], out var hours)
            || !long.TryParse(components[1], out var minutes)
            || !long.TryParse(secondsAndMilliseconds[0], out var seconds)
            || !TryParseSrtFraction(
                secondsAndMilliseconds.Length == 2 ? secondsAndMilliseconds[1] : null,
                out var parsedMilliseconds)
            || minutes is < -59 or > 59
            || seconds is < -59 or > 59)
        {
            return false;
        }

        var componentNegative = hours < 0 || minutes < 0 || seconds < 0;
        hasNegativeComponent |= componentNegative || parsedMilliseconds < 0;

        try
        {
            if (overallNegative || componentNegative)
            {
                var magnitudeSeconds = checked(
                    ((Math.Abs(hours) * 60) + Math.Abs(minutes)) * 60 + Math.Abs(seconds));
                milliseconds = checked(-(magnitudeSeconds * 1000 + Math.Abs(parsedMilliseconds)));
            }
            else
            {
                var wholeSeconds = checked(((hours * 60) + minutes) * 60 + seconds);
                milliseconds = checked(wholeSeconds * 1000 + parsedMilliseconds);
            }

            return true;
        }
        catch (OverflowException)
        {
            milliseconds = 0;
            return false;
        }
    }

    private static bool TryParseSrtFraction(string? value, out long milliseconds)
    {
        milliseconds = 0;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        var negative = value.StartsWith("-", StringComparison.Ordinal);
        var digits = value.TrimStart('+', '-');
        if (digits.Length is < 1 or > 3 || !long.TryParse(digits, out var parsed))
        {
            return false;
        }

        if (negative)
        {
            milliseconds = -parsed;
            return true;
        }

        milliseconds = long.Parse(digits.PadRight(3, '0'));
        return true;
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool TryParseAssTimestamp(
        string value,
        out long milliseconds,
        out bool hasNegativeComponent)
    {
        milliseconds = 0;
        hasNegativeComponent = false;
        var timestamp = value.Trim().Replace(',', '.');
        var overallNegative = false;
        if (timestamp.StartsWith("-", StringComparison.Ordinal))
        {
            overallNegative = true;
            hasNegativeComponent = true;
            timestamp = timestamp[1..];
        }
        else if (timestamp.StartsWith("+", StringComparison.Ordinal))
        {
            timestamp = timestamp[1..];
        }

        var components = timestamp.Split(':');
        if (components.Length != 3)
        {
            return false;
        }

        var secondsAndFraction = components[2].Split('.');
        if (secondsAndFraction.Length is < 1 or > 2
            || !long.TryParse(components[0], out var hours)
            || !long.TryParse(components[1], out var minutes)
            || !long.TryParse(secondsAndFraction[0], out var seconds)
            || !TryParseAssFraction(
                secondsAndFraction.Length == 2 ? secondsAndFraction[1] : null,
                out var parsedMilliseconds)
            || minutes is < -59 or > 59
            || seconds is < -59 or > 59)
        {
            return false;
        }

        var componentNegative = hours < 0 || minutes < 0 || seconds < 0;
        hasNegativeComponent |= componentNegative || parsedMilliseconds < 0;

        try
        {
            if (overallNegative || componentNegative)
            {
                var magnitudeSeconds = checked(
                    ((Math.Abs(hours) * 60) + Math.Abs(minutes)) * 60 + Math.Abs(seconds));
                milliseconds = checked(-(magnitudeSeconds * 1000 + Math.Abs(parsedMilliseconds)));
            }
            else
            {
                var wholeSeconds = checked(((hours * 60) + minutes) * 60 + seconds);
                milliseconds = checked(wholeSeconds * 1000 + parsedMilliseconds);
            }

            return true;
        }
        catch (OverflowException)
        {
            milliseconds = 0;
            return false;
        }
    }

    private static bool TryParseAssFraction(string? value, out long milliseconds)
    {
        milliseconds = 0;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        var negative = value.StartsWith("-", StringComparison.Ordinal);
        var digits = value.TrimStart('+', '-');
        if (digits.Length is < 1 or > 3 || !long.TryParse(digits, out var parsed))
        {
            return false;
        }

        milliseconds = long.Parse(digits.PadRight(3, '0'));
        if (negative)
        {
            milliseconds = -milliseconds;
        }

        return true;
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
        @"^[ \t]*(?<start>\S+:\S+:\S+)[ \t]*-->[ \t]*(?<end>\S+:\S+:\S+)(?<suffix>[^\r\n]*)$")]
    private static partial Regex SrtTimestampLineRegex();

    [GeneratedRegex(@"^[ \t]*[+-]?\d+[ \t]*$")]
    private static partial Regex SrtSequenceNumberRegex();

    [GeneratedRegex(
        @"^(?<prefix>[ \t]*Dialogue[ \t]*:[^,\r\n]*,)(?<start>[^,\r\n]*),(?<end>[^,\r\n]*)(?<suffix>,[^\r\n]*)(?<cr>\r?)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AssDialogueLineRegex();

    [GeneratedRegex(
        @"(?<prefix><sync\b[^>]*\bstart\s*=\s*[""']?)(?<value>-\d+)(?<suffix>[""']?[^>]*>)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SmiSyncTagRegex();

    private sealed record SrtCueCandidate(
        int CueStartLine,
        int TimestampLine,
        int BodyStartLine,
        string OriginalStart,
        string OriginalEnd,
        string Suffix,
        bool Parsed,
        long StartMilliseconds,
        long EndMilliseconds,
        bool HasNegativeComponent);
}
