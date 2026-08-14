using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SubMuxBatch.Core.External;

public static partial class SubtitleCompatibilityNormalizer
{
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
}
