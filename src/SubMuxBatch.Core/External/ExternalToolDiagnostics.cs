namespace SubMuxBatch.Core.External;

internal static class ExternalToolDiagnostics
{
    internal static IReadOnlyList<string> FilterSuccessfulProbeNoise(params string?[] outputs) =>
        FilterSuccessfulProbeNoise(outputs.SelectMany(ReadLines));

    internal static IReadOnlyList<string> FilterSuccessfulProbeNoise(IEnumerable<string> lines)
    {
        var result = new List<string>();
        var suppressedProbeWarning = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Contains("Could not find codec parameters for stream", StringComparison.OrdinalIgnoreCase))
            {
                suppressedProbeWarning = true;
                continue;
            }

            if (suppressedProbeWarning
                && line.Contains("Consider increasing the value for the 'analyzeduration'", StringComparison.OrdinalIgnoreCase)
                && line.Contains("'probesize'", StringComparison.OrdinalIgnoreCase))
            {
                suppressedProbeWarning = false;
                continue;
            }

            suppressedProbeWarning = false;
            if (!result.Contains(line, StringComparer.Ordinal))
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static IEnumerable<string> ReadLines(string? output) =>
        string.IsNullOrWhiteSpace(output)
            ? []
            : output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
