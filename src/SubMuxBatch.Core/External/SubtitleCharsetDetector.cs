using System.Text;

namespace SubMuxBatch.Core.External;

public static class SubtitleCharsetDetector
{
    public static string? DetectForMkvMerge(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return "UTF-8";
        }

        if (bytes.Length >= 2
            && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return null;
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return "UTF-8";
        }
        catch (DecoderFallbackException)
        {
            return "CP949";
        }
    }
}
