using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;

namespace SubMuxBatch.Core.External;

internal static partial class LibSeFontColourResolver
{
    private static readonly ConcurrentDictionary<string, FontColourResolution> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static FontColourResolution Resolve(string value)
    {
        var normalized = value.Trim();
        return Cache.GetOrAdd(normalized, static candidate => ResolveUncached(candidate));
    }

    private static FontColourResolution ResolveUncached(string value)
    {
        try
        {
            var rgbFunction = RgbFunctionRegex().Match(value);
            if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)
                && (!rgbFunction.Success || !RgbComponentsAreValid(rgbFunction)))
            {
                return new FontColourResolution(false, null);
            }

            var colour = rgbFunction.Success
                ? HtmlUtil.GetColorFromString(value)
                : ColorTranslator.FromHtml(value);
            return new FontColourResolution(
                true,
                $"#{colour.Red:X2}{colour.Green:X2}{colour.Blue:X2}");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return new FontColourResolution(false, null);
        }
    }

    private static bool RgbComponentsAreValid(Match match)
    {
        if (!byte.TryParse(match.Groups["r"].Value, out _)
            || !byte.TryParse(match.Groups["g"].Value, out _)
            || !byte.TryParse(match.Groups["b"].Value, out _))
        {
            return false;
        }

        var alpha = match.Groups["a"].Value;
        return alpha.Length == 0
               || double.TryParse(
                   alpha,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var parsedAlpha)
               && parsedAlpha is >= 0 and <= 1;
    }

    [GeneratedRegex(
        @"^rgb(?:a)?\(\s*(?<r>\d{1,3})\s*,\s*(?<g>\d{1,3})\s*,\s*(?<b>\d{1,3})(?:\s*,\s*(?<a>(?:\d+(?:\.\d*)?|\.\d+)))?\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RgbFunctionRegex();
}

internal sealed record FontColourResolution(bool Recognized, string? CanonicalRgb);
