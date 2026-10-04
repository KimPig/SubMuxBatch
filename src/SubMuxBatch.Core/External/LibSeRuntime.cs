using System.Text;
using Nikse.SubtitleEdit.Core.Common;

namespace SubMuxBatch.Core.External;

internal static class LibSeRuntime
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static SemaphoreSlim Gate { get; } = new(1, 1);

    static LibSeRuntime() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static async Task<Subtitle?> ParseAsync(
        string inputPath,
        CancellationToken cancellationToken)
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

        return Subtitle.Parse(inputPath, Encoding.GetEncoding(949));
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
}
