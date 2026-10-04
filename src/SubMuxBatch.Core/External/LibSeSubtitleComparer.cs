using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.External;

/// <summary>
/// Compares subtitle files by the plain cue text and timing that libse reads.
/// File order, cue numbering and format-specific styling do not affect the result.
/// </summary>
public static class LibSeSubtitleComparer
{
    private const long TimingToleranceMilliseconds = 15;

    public static async Task<bool> AreEquivalentAsync(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightPath);

        await LibSeRuntime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var left = await ReadCuesAsync(leftPath, cancellationToken).ConfigureAwait(false);
            var right = await ReadCuesAsync(rightPath, cancellationToken).ConfigureAwait(false);
            if (left.Length == 0 || left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                if (Math.Abs(left[index].StartMilliseconds - right[index].StartMilliseconds)
                    > TimingToleranceMilliseconds
                    || Math.Abs(left[index].EndMilliseconds - right[index].EndMilliseconds)
                    > TimingToleranceMilliseconds
                    || !string.Equals(left[index].Text, right[index].Text, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            LibSeRuntime.Gate.Release();
        }
    }

    private static async Task<PlainCue[]> ReadCuesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(CoreText.Get("Subtitle_InputNotFound"), resolvedPath);
        }

        var subtitle = await LibSeRuntime.ParseAsync(resolvedPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(CoreText.Get("Subtitle_UnknownFormat", Path.GetFileName(resolvedPath)));
        var sourceFormat = subtitle.OriginalFormat
            ?? throw new InvalidDataException(CoreText.Get("Subtitle_UnknownFormat", Path.GetFileName(resolvedPath)));
        var subRip = new SubRip();
        if (!string.Equals(sourceFormat.Name, subRip.Name, StringComparison.Ordinal))
        {
            sourceFormat.RemoveNativeFormatting(subtitle, subRip);
        }

        return subtitle.Paragraphs
            .Select(static paragraph => new PlainCue(
                (long)Math.Round(paragraph.StartTime.TotalMilliseconds),
                (long)Math.Round(paragraph.EndTime.TotalMilliseconds),
                NormalizeText(paragraph.Text)))
            .Where(static cue => cue.Text.Length > 0)
            .OrderBy(static cue => cue.StartMilliseconds)
            .ThenBy(static cue => cue.EndMilliseconds)
            .ThenBy(static cue => cue.Text, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeText(string text)
    {
        var plain = HtmlUtil.RemoveHtmlTags(text, true)
            .Replace("\\N", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("\\h", " ", StringComparison.OrdinalIgnoreCase);
        plain = WebUtility.HtmlDecode(plain)
            .Replace('\u00A0', ' ')
            .Normalize(NormalizationForm.FormC);
        return string.Join(
                '\n',
                plain.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n')
                    .Select(static line => line.Trim()))
            .Trim();
    }

    private sealed record PlainCue(long StartMilliseconds, long EndMilliseconds, string Text);
}
