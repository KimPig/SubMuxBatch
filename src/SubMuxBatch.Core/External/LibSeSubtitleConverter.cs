using System.Text;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.External;

public enum SubtitleOutputFormat
{
    SubRip,
    AdvancedSubStationAlpha
}

public sealed record SubtitleConversionResult(
    IReadOnlyList<string> Warnings,
    string InputFormat,
    string? InputEncoding,
    int CueCount);

public interface ISubtitleConverter
{
    Task<SubtitleConversionResult> ConvertAsync(
        string inputPath,
        string outputPath,
        SubtitleOutputFormat outputFormat,
        string? assStylePath = null,
        int playResX = AdvancedSubStationAlpha.DefaultWidth,
        int playResY = AdvancedSubStationAlpha.DefaultHeight,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-process subtitle converter using the same libse 5.1 formatting-removal
/// path that Subtitle Edit invokes before saving to a different format.
/// </summary>
public sealed partial class LibSeSubtitleConverter : ISubtitleConverter
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UTF8Encoding OutputUtf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly SemaphoreSlim ConversionGate = new(1, 1);

    static LibSeSubtitleConverter() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task<SubtitleConversionResult> ConvertAsync(
        string inputPath,
        string outputPath,
        SubtitleOutputFormat outputFormat,
        string? assStylePath = null,
        int playResX = AdvancedSubStationAlpha.DefaultWidth,
        int playResY = AdvancedSubStationAlpha.DefaultHeight,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedInput = Path.GetFullPath(inputPath);
        var resolvedOutput = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(resolvedOutput)
            ?? throw new ArgumentException(CoreText.Get("Subtitle_NoOutputFolder"), nameof(outputPath));

        if (!File.Exists(resolvedInput))
        {
            throw new FileNotFoundException(CoreText.Get("Subtitle_InputNotFound"), resolvedInput);
        }

        if (File.Exists(resolvedOutput))
        {
            throw new IOException(CoreText.Get("Subtitle_OutputExists", resolvedOutput));
        }

        Subtitle subtitle;
        SubtitleFormat sourceFormat;
        string outputText;
        await ConversionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            subtitle = await ParseAsync(resolvedInput, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(CoreText.Get("Subtitle_UnknownFormat", Path.GetFileName(resolvedInput)));
            sourceFormat = subtitle.OriginalFormat
                ?? throw new InvalidOperationException(CoreText.Get("Subtitle_UnknownFormat", Path.GetFileName(resolvedInput)));

            SubtitleFormat outputWriter = outputFormat switch
            {
                SubtitleOutputFormat.SubRip => new SubRip(),
                SubtitleOutputFormat.AdvancedSubStationAlpha => new AdvancedSubStationAlpha(),
                _ => throw new ArgumentOutOfRangeException(nameof(outputFormat))
            };

            // Subtitle Edit's GUI invokes this immediately before Save As.
            if (!string.Equals(sourceFormat.Name, outputWriter.Name, StringComparison.Ordinal))
            {
                sourceFormat.RemoveNativeFormatting(subtitle, outputWriter);
            }

            if (outputFormat == SubtitleOutputFormat.AdvancedSubStationAlpha)
            {
                subtitle.Header = await CreateAssHeaderAsync(
                        assStylePath,
                        playResX,
                        playResY,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            outputText = outputWriter.ToText(subtitle, Path.GetFileNameWithoutExtension(resolvedInput));
            ValidateOutput(outputText, outputFormat);
        }
        finally
        {
            ConversionGate.Release();
        }

        Directory.CreateDirectory(outputDirectory);
        var temporaryOutput = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(resolvedOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryOutput, outputText, OutputUtf8, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryOutput, resolvedOutput, overwrite: false);
        }
        finally
        {
            TryDelete(temporaryOutput);
        }

        return new SubtitleConversionResult(
            [],
            sourceFormat.FriendlyName ?? string.Empty,
            subtitle.OriginalEncoding?.WebName,
            subtitle.Paragraphs.Count);
    }

    private static async Task<Subtitle?> ParseAsync(string inputPath, CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(inputPath).Equals(".smi", StringComparison.OrdinalIgnoreCase))
        {
            return Subtitle.Parse(inputPath);
        }

        var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken).ConfigureAwait(false);
        if (HasUnicodeBom(bytes) || IsValidUtf8(bytes))
        {
            return Subtitle.Parse(inputPath);
        }

        // Preserve the application's established CP949 fallback policy.
        return Subtitle.Parse(inputPath, Encoding.GetEncoding(949));
    }

    private static async Task<string> CreateAssHeaderAsync(
        string? assStylePath,
        int playResX,
        int playResY,
        CancellationToken cancellationToken)
    {
        string header;
        if (!string.IsNullOrWhiteSpace(assStylePath))
        {
            if (!File.Exists(assStylePath))
            {
                throw new InvalidOperationException(CoreText.Get("Subtitle_StyleFileMissing"));
            }

            header = await File.ReadAllTextAsync(assStylePath, cancellationToken).ConfigureAwait(false);
            header = RemoveEventsFormatLine(header);
        }
        else
        {
            header = AdvancedSubStationAlpha.DefaultHeader;
        }

        header = AdvancedSubStationAlpha.AddTagToHeader(
            "PlayResX",
            $"PlayResX: {playResX}",
            "[Script Info]",
            header);
        return AdvancedSubStationAlpha.AddTagToHeader(
            "PlayResY",
            $"PlayResY: {playResY}",
            "[Script Info]",
            header);
    }

    private static string RemoveEventsFormatLine(string header)
    {
        var lines = header.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder(header.Length);
        var inEvents = false;
        foreach (var line in lines)
        {
            if (line.Trim().Equals("[Events]", StringComparison.OrdinalIgnoreCase))
            {
                inEvents = true;
            }
            else if (line.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                inEvents = false;
            }

            if (inEvents && line.TrimStart().StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private static void ValidateOutput(string outputText, SubtitleOutputFormat outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException(CoreText.Get("Subtitle_OutputEmpty"));
        }

        if (outputFormat == SubtitleOutputFormat.SubRip)
        {
            if (!SrtTimecodeRegex().IsMatch(outputText))
            {
                throw new InvalidOperationException(CoreText.Get("Subtitle_InvalidSrtTimecode"));
            }

            if (AssEventLeakRegex().IsMatch(outputText))
            {
                throw new InvalidOperationException(CoreText.Get("Subtitle_AssFieldsLeaked"));
            }

            return;
        }

        if (!outputText.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)
            || !outputText.Contains("[Events]", StringComparison.OrdinalIgnoreCase)
            || !outputText.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(CoreText.Get("Subtitle_InvalidAssSections"));
        }
    }

    private static bool HasUnicodeBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })
        || bytes.StartsWith(new byte[] { 0xFF, 0xFE })
        || bytes.StartsWith(new byte[] { 0xFE, 0xFF })
        || bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })
        || bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF });

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            _ = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup. The original exception, if any, is more useful.
        }
    }

    [GeneratedRegex(@"(?m)^\d{2}:\d{2}:\d{2},\d{3}\s+-->\s+\d{2}:\d{2}:\d{2},\d{3}\s*$")]
    private static partial Regex SrtTimecodeRegex();

    [GeneratedRegex(@"(?m)^[^\r\n]*\bDefault\s*,\s*,\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*,")]
    private static partial Regex AssEventLeakRegex();
}
