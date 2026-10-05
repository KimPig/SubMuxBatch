using System.Diagnostics;
using System.Globalization;

namespace SubMuxBatch.Core.Media;

public sealed class FfmpegPacketSizeAnalyzer(string executablePath)
{
    public async Task<MediaInfoStreamSizeReport> AnalyzeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The media file was not found.", path);
        }
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("FFmpeg was not found.", executablePath);
        }

        path = Path.GetFullPath(path);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-i", path,
                     "-map", "0:v?", "-map", "0:a?", "-map", "0:s?",
                     "-c", "copy", "-f", "framehash", "-hash", "crc32", "pipe:1"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg could not be started.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Cancellation is reported by the awaiting caller.
            }
        });

        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var streamTypes = new Dictionary<int, string>();
        var streamBytes = new Dictionary<int, long>();
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (TryParseMediaTypeLine(line, out var outputIndex, out var mediaType))
            {
                streamTypes[outputIndex] = mediaType;
                continue;
            }

            if (TryParseFrameHashLine(line, out outputIndex, out var packetBytes))
            {
                streamBytes[outputIndex] = checked(streamBytes.GetValueOrDefault(outputIndex) + packetBytes);
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(standardError)
                    ? $"FFmpeg exited with code {process.ExitCode}."
                    : standardError.Trim());
        }

        return new MediaInfoStreamSizeReport(
            SizesForType("video"),
            SizesForType("audio"),
            SizesForType("subtitle"));

        IReadOnlyList<long?> SizesForType(string type) => streamTypes
            .Where(pair => string.Equals(pair.Value, type, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static pair => pair.Key)
            .Select(pair => (long?)streamBytes.GetValueOrDefault(pair.Key))
            .ToArray();
    }

    internal static bool TryParseMediaTypeLine(string line, out int outputIndex, out string mediaType)
    {
        outputIndex = -1;
        mediaType = string.Empty;
        const string prefix = "#media_type ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = line.IndexOf(':', prefix.Length);
        if (separator < 0
            || !int.TryParse(line.AsSpan(prefix.Length, separator - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out outputIndex))
        {
            return false;
        }

        mediaType = line[(separator + 1)..].Trim();
        return mediaType.Length > 0;
    }

    internal static bool TryParseFrameHashLine(string line, out int outputIndex, out long packetBytes)
    {
        outputIndex = -1;
        packetBytes = 0;
        if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
        {
            return false;
        }

        var firstComma = line.IndexOf(',');
        if (firstComma <= 0
            || !int.TryParse(line.AsSpan(0, firstComma).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out outputIndex))
        {
            return false;
        }

        var fieldStart = firstComma + 1;
        for (var field = 1; field < 4; field++)
        {
            var comma = line.IndexOf(',', fieldStart);
            if (comma < 0)
            {
                return false;
            }
            fieldStart = comma + 1;
        }

        var fieldEnd = line.IndexOf(',', fieldStart);
        return fieldEnd > fieldStart
               && long.TryParse(
                   line.AsSpan(fieldStart, fieldEnd - fieldStart).Trim(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out packetBytes)
               && packetBytes >= 0;
    }
}
