using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SubMuxBatch.Core.Localization;

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
    private const double DefaultRubyBaseFontSize = 75;
    private const double RubyFontSizeRatio = 0.5;

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
        await PrepareSrtForAssAsync(
            sourcePath,
            outputPath,
            DefaultRubyBaseFontSize,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task PrepareSrtForAssAsync(
        string sourcePath,
        string outputPath,
        double rubyBaseFontSize,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(rubyBaseFontSize) || rubyBaseFontSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rubyBaseFontSize));
        }

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var text = DecodeSubtitle(bytes);
        ValidateSrtFontColours(text);
        var normalized = FlattenRuby(text, rubyBaseFontSize * RubyFontSizeRatio);
        normalized = NormalizeSupportedHtmlTags(normalized);
        await File.WriteAllTextAsync(
            outputPath,
            normalized,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task ValidateSrtFormattingForAssAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        ValidateSrtFontColours(DecodeSubtitle(bytes));
    }

    private static void ValidateSrtFontColours(string text)
    {
        var invalid = new List<InvalidFontColour>();
        var fallbackCueNumber = 0;
        foreach (var block in SrtBlockSeparatorRegex().Split(NormalizeLineEndings(text)))
        {
            var lines = block.Split('\n');
            var timestampIndex = Array.FindIndex(lines, line => SrtTimestampLineRegex().IsMatch(line));
            if (timestampIndex < 0)
            {
                continue;
            }

            fallbackCueNumber++;
            var cueNumber = timestampIndex > 0
                            && int.TryParse(
                                lines[timestampIndex - 1].Trim(),
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var parsedCueNumber)
                ? parsedCueNumber
                : fallbackCueNumber;
            var timestamp = SrtTimestampLineRegex().Match(lines[timestampIndex]);
            var start = timestamp.Groups["start"].Value.Replace(',', '.');
            var cueText = string.Join('\n', lines[(timestampIndex + 1)..]);
            foreach (Match fontTag in FontTagRegex().Matches(cueText))
            {
                var colour = ParseNormalizedFontAttributes(fontTag.Value)
                    .FirstOrDefault(attribute => string.Equals(
                        attribute.Name,
                        "color",
                        StringComparison.OrdinalIgnoreCase));
                if (colour is not null && !FontHexColourRegex().IsMatch(colour.Value.Trim()))
                {
                    invalid.Add(new InvalidFontColour(colour.Value.Trim(), cueNumber, start));
                }
            }
        }

        if (invalid.Count == 0)
        {
            return;
        }

        var first = invalid[0];
        throw new InvalidDataException(CoreText.Get(
            "Subtitle_UnsupportedFontColour",
            first.Value,
            invalid.Count,
            first.CueNumber,
            first.Start));
    }

    public static async Task<int> PrepareAssForSrtAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var text = DecodeSubtitle(bytes);
        var lines = NormalizeLineEndings(text).Split('\n');
        var output = new List<string>(lines.Length);
        var inEventsSection = false;
        var dialogueCount = 0;

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = AssSectionLineRegex().Match(line);
            if (section.Success)
            {
                inEventsSection = string.Equals(
                    section.Groups["name"].Value.Trim(),
                    "Events",
                    StringComparison.OrdinalIgnoreCase);
            }

            if (inEventsSection && AssCommentPrefixRegex().IsMatch(line))
            {
                continue;
            }

            if (inEventsSection && AssDialoguePrefixRegex().IsMatch(line))
            {
                dialogueCount++;
            }

            output.Add(line);
        }

        await File.WriteAllTextAsync(
            outputPath,
            string.Join("\r\n", output),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        return dialogueCount;
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

    private static string FlattenRuby(string text, double rubyFontSize)
    {
        var size = rubyFontSize.ToString("0.##", CultureInfo.InvariantCulture);
        var result = RubyBlockRegex().Replace(text, match =>
        {
            var content = match.Groups["content"].Value;
            content = RubyParenthesisRegex().Replace(content, string.Empty);
            content = RubyBaseTagRegex().Replace(content, string.Empty);
            return RubyTextRegex().Replace(content, readingMatch =>
            {
                var reading = StripTags(readingMatch.Groups["text"].Value).Trim();
                return reading.Length == 0
                    ? string.Empty
                    : $"<font size=\"{size}\">{WebUtility.HtmlEncode(reading)}</font>";
            });
        });

        // Avoid literal tag leakage for malformed or unclosed ruby fragments.
        return OrphanRubyTagRegex().Replace(result, string.Empty);
    }

    private static string NormalizeSupportedHtmlTags(string text)
    {
        var normalized = SupportedTagNameRegex().Replace(text, static match =>
            $"<{match.Groups["slash"].Value}{match.Groups["name"].Value.ToLowerInvariant()}");

        normalized = FontTagRegex().Replace(normalized, static tag => NormalizeFontTag(tag.Value));
        return FlattenFontTagsPerCue(normalized);
    }

    private static string FlattenFontTagsPerCue(string text)
    {
        var parts = SrtBlockSeparatorRegex().Split(text);
        for (var index = 0; index < parts.Length; index += 2)
        {
            parts[index] = FlattenFontTagsInBlock(parts[index]);
        }

        return string.Concat(parts);
    }

    private static string FlattenFontTagsInBlock(string block)
    {
        var position = 0;
        var builder = new StringBuilder(block.Length + 32);
        var states = new Stack<IReadOnlyList<FontAttribute>>();
        states.Push([]);

        foreach (Match match in AnyFontTagRegex().Matches(block))
        {
            AppendTextWithFontState(
                builder,
                block.AsSpan(position, match.Index - position),
                states.Peek());

            var isClosing = match.Value.StartsWith("</", StringComparison.OrdinalIgnoreCase);
            var isSelfClosing = match.Value.TrimEnd().EndsWith("/>", StringComparison.Ordinal);
            if (isClosing)
            {
                if (states.Count > 1)
                {
                    states.Pop();
                }
            }
            else if (!isSelfClosing)
            {
                states.Push(MergeFontAttributes(states.Peek(), ParseNormalizedFontAttributes(match.Value)));
            }

            position = match.Index + match.Length;
        }

        AppendTextWithFontState(builder, block.AsSpan(position), states.Peek());
        return builder.ToString();
    }

    private static void AppendTextWithFontState(
        StringBuilder builder,
        ReadOnlySpan<char> text,
        IReadOnlyList<FontAttribute> attributes)
    {
        if (text.IsEmpty)
        {
            return;
        }

        if (attributes.Count == 0)
        {
            builder.Append(text);
            return;
        }

        var trailingStart = text.Length;
        while (trailingStart > 0 && text[trailingStart - 1] is '\r' or '\n')
        {
            trailingStart--;
        }

        if (trailingStart == 0)
        {
            builder.Append(text);
            return;
        }

        AppendFontOpenTag(builder, attributes);
        builder.Append(text[..trailingStart]);
        builder.Append("</font>");
        builder.Append(text[trailingStart..]);
    }

    private static IReadOnlyList<FontAttribute> ParseNormalizedFontAttributes(string tag)
    {
        var bodyStart = tag.IndexOf("font", StringComparison.OrdinalIgnoreCase) + 4;
        var bodyEnd = tag.LastIndexOf('>');
        var body = tag[bodyStart..bodyEnd].TrimEnd();
        if (body.EndsWith("/", StringComparison.Ordinal))
        {
            body = body[..^1];
        }

        return ParseFontAttributes(body, tag);
    }

    private static IReadOnlyList<FontAttribute> MergeFontAttributes(
        IReadOnlyList<FontAttribute> inherited,
        IReadOnlyList<FontAttribute> overrides)
    {
        var merged = inherited.ToList();
        foreach (var attribute in overrides)
        {
            var existing = merged.FindIndex(item =>
                string.Equals(item.Name, attribute.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                merged[existing] = attribute;
            }
            else
            {
                merged.Add(attribute);
            }
        }

        return merged;
    }

    private static void AppendFontOpenTag(StringBuilder builder, IReadOnlyList<FontAttribute> attributes)
    {
        builder.Append("<font");
        foreach (var attribute in attributes)
        {
            builder.Append(' ')
                .Append(attribute.Name.ToLowerInvariant())
                .Append("=\"")
                .Append(WebUtility.HtmlEncode(attribute.Value))
                .Append('"');
        }

        builder.Append('>');
    }

    private static string NormalizeFontTag(string tag)
    {
        var bodyStart = tag.IndexOf("font", StringComparison.OrdinalIgnoreCase) + 4;
        var bodyEnd = tag.LastIndexOf('>');
        if (bodyStart < 4 || bodyEnd < bodyStart)
        {
            throw new InvalidDataException(CoreText.Get("Subtitle_InvalidFontTag", tag));
        }

        var body = tag[bodyStart..bodyEnd];
        var selfClosing = body.TrimEnd().EndsWith("/", StringComparison.Ordinal);
        if (selfClosing)
        {
            body = body.TrimEnd();
            body = body[..^1];
        }

        var attributes = ParseFontAttributes(body, tag);
        if (attributes.Count == 0)
        {
            return selfClosing ? "<font />" : "<font>";
        }

        var builder = new StringBuilder("<font");
        foreach (var attribute in attributes)
        {
            var name = attribute.Name.ToLowerInvariant();
            var value = attribute.Value.Trim();
            if (value.Length == 0)
            {
                throw new InvalidDataException(CoreText.Get("Subtitle_EmptyFontAttribute", name));
            }

            if (string.Equals(name, "color", StringComparison.OrdinalIgnoreCase))
            {
                var colour = FontHexColourRegex().Match(value);
                if (colour.Success)
                {
                    value = $"#{colour.Groups["hex"].Value.ToUpperInvariant()}";
                }
            }

            builder.Append(' ')
                .Append(name)
                .Append("=\"")
                .Append(WebUtility.HtmlEncode(value))
                .Append('"');
        }

        builder.Append(selfClosing ? " />" : ">");
        return builder.ToString();
    }

    private static List<FontAttribute> ParseFontAttributes(string body, string originalTag)
    {
        var attributes = new List<FontAttribute>();
        var index = 0;
        while (index < body.Length)
        {
            while (index < body.Length && char.IsWhiteSpace(body[index])) index++;
            if (index >= body.Length) break;

            var nameStart = index;
            while (index < body.Length && !char.IsWhiteSpace(body[index]) && body[index] != '=') index++;
            var name = body[nameStart..index];
            while (index < body.Length && char.IsWhiteSpace(body[index])) index++;
            if (name.Length == 0 || index >= body.Length || body[index] != '=')
            {
                throw new InvalidDataException(CoreText.Get("Subtitle_InvalidFontTag", originalTag));
            }

            index++;
            while (index < body.Length && char.IsWhiteSpace(body[index])) index++;
            if (index >= body.Length)
            {
                throw new InvalidDataException(CoreText.Get("Subtitle_EmptyFontAttribute", name));
            }

            string value;
            if (body[index] is '"' or '\'')
            {
                var quote = body[index++];
                var valueStart = index;
                while (index < body.Length && body[index] != quote) index++;
                if (index >= body.Length)
                {
                    throw new InvalidDataException(CoreText.Get("Subtitle_InvalidFontTag", originalTag));
                }

                value = body[valueStart..index];
                index++;
            }
            else
            {
                var valueStart = index;
                index = FindNextFontAttribute(body, index);
                value = body[valueStart..index].TrimEnd();
            }

            attributes.Add(new FontAttribute(name, WebUtility.HtmlDecode(value)));
        }

        return attributes;
    }

    private static int FindNextFontAttribute(string body, int valueStart)
    {
        for (var index = valueStart; index < body.Length; index++)
        {
            if (!char.IsWhiteSpace(body[index])) continue;
            var candidate = index;
            while (candidate < body.Length && char.IsWhiteSpace(body[candidate])) candidate++;
            var nameStart = candidate;
            while (candidate < body.Length && !char.IsWhiteSpace(body[candidate]) && body[candidate] != '=') candidate++;
            var name = body[nameStart..candidate];
            while (candidate < body.Length && char.IsWhiteSpace(body[candidate])) candidate++;
            if (candidate < body.Length
                && body[candidate] == '='
                && name.Length > 0)
            {
                return index;
            }
        }

        return body.Length;
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

    [GeneratedRegex(@"</?font\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AnyFontTagRegex();

    [GeneratedRegex(@"(\r?\n[\t ]*\r?\n)")]
    private static partial Regex SrtBlockSeparatorRegex();

    [GeneratedRegex(@"^#?(?<hex>[0-9a-f]{6})$", RegexOptions.IgnoreCase)]
    private static partial Regex FontHexColourRegex();

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

    [GeneratedRegex(@"^[ \t]*\[(?<name>[^\]]+)\][ \t]*$")]
    private static partial Regex AssSectionLineRegex();

    [GeneratedRegex(@"^[ \t]*Comment[ \t]*:", RegexOptions.IgnoreCase)]
    private static partial Regex AssCommentPrefixRegex();

    private sealed record FontAttribute(string Name, string Value);

    private sealed record InvalidFontColour(string Value, int CueNumber, string Start);

    [GeneratedRegex(@"^[ \t]*Dialogue[ \t]*:", RegexOptions.IgnoreCase)]
    private static partial Regex AssDialoguePrefixRegex();

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
