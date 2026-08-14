using System.Text.Json;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.External;

public enum SubtitleOutputFormat
{
    SubRip,
    AdvancedSubStationAlpha
}

public sealed record SeConvResult(IReadOnlyList<string> Warnings);

public sealed class SeConvClient(string executablePath, IProcessRunner processRunner)
{
    public async Task<SeConvResult> ConvertAsync(
        string inputPath,
        string outputPath,
        SubtitleOutputFormat outputFormat,
        string? assStylePath,
        int playResX,
        int playResY,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedInput = Path.GetFullPath(inputPath);
        var resolvedOutput = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(resolvedOutput)
            ?? throw new ArgumentException(CoreText.Get("SeConv_NoOutputFolder"), nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);

        if (!File.Exists(resolvedInput))
        {
            throw new FileNotFoundException(CoreText.Get("SeConv_InputNotFound"), resolvedInput);
        }

        if (File.Exists(resolvedOutput))
        {
            throw new IOException(CoreText.Get("SeConv_OutputExists", resolvedOutput));
        }

        // seconv 5.1.0 treats square brackets in path arguments as Spectre.Console
        // markup. Stage every argument under safe names and pass relative paths only.
        var operationId = Guid.NewGuid().ToString("N");
        var stagedInputName = $"seconv-{operationId}-input{SafeExtension(resolvedInput, ".sub")}";
        var stagedOutputName = $"seconv-{operationId}-output{OutputExtension(outputFormat)}";
        var stagedStyleName = $"seconv-{operationId}-style.ass";
        var stagedInputPath = Path.Combine(outputDirectory, stagedInputName);
        var stagedOutputPath = Path.Combine(outputDirectory, stagedOutputName);
        var stagedStylePath = Path.Combine(outputDirectory, stagedStyleName);

        try
        {
            File.Copy(resolvedInput, stagedInputPath, overwrite: false);

            var formatName = outputFormat == SubtitleOutputFormat.SubRip ? "subrip" : "assa";
            var arguments = new List<string>
            {
                stagedInputName,
                formatName,
                "--output-folder:.",
                $"--output-filename:{stagedOutputName}",
                "--input-encoding-fallback:949",
                "--overwrite",
                "--json"
            };

            if (outputFormat == SubtitleOutputFormat.AdvancedSubStationAlpha)
            {
                arguments.Add($"--resolution:{playResX}x{playResY}");

                if (!string.IsNullOrWhiteSpace(assStylePath))
                {
                    if (!File.Exists(assStylePath))
                    {
                        throw new InvalidOperationException(CoreText.Get("SeConv_StyleFileMissing"));
                    }

                    File.Copy(Path.GetFullPath(assStylePath), stagedStylePath, overwrite: false);
                    arguments.Add($"--assa-style-file:{stagedStyleName}");
                }
            }

            var result = await processRunner.RunAsync(
                new ProcessRequest(executablePath, arguments, outputDirectory),
                onOutput: null,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(BuildFailureMessage(CoreText.Get("SeConv_ConversionFailed"), result));
            }

            var warnings = ValidateJsonResult(result.StandardOutput, stagedOutputPath, outputDirectory);
            ValidateOutputFile(stagedOutputPath, outputFormat);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedOutputPath, resolvedOutput, overwrite: false);

            foreach (var warning in warnings)
            {
                onOutput?.Invoke(CoreText.Get("SeConv_Warning", warning));
            }

            return new SeConvResult(warnings);
        }
        finally
        {
            TryDelete(stagedInputPath);
            TryDelete(stagedStylePath);
            TryDelete(stagedOutputPath);
        }
    }

    private static IReadOnlyList<string> ValidateJsonResult(
        string output,
        string expectedOutputPath,
        string workingDirectory)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException(CoreText.Get("SeConv_InvalidJsonResult"));
        }

        try
        {
            using var document = JsonDocument.Parse(output[start..(end + 1)]);
            var root = document.RootElement;
            var success = GetBoolean(root, "success");
            var totalFiles = GetInt32(root, "totalFiles");
            var successfulFiles = GetInt32(root, "successfulFiles");
            var failedFiles = GetInt32(root, "failedFiles");

            if (!success || totalFiles != 1 || successfulFiles != 1 || failedFiles != 0)
            {
                throw new InvalidOperationException(CoreText.Get("SeConv_ReportedFailure"));
            }

            if (!root.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array
                || files.GetArrayLength() != 1)
            {
                throw new InvalidOperationException(CoreText.Get("SeConv_InvalidFileList"));
            }

            var file = files[0];
            if (!GetBoolean(file, "success"))
            {
                throw new InvalidOperationException(CoreText.Get("SeConv_InputConversionFailed"));
            }

            var reportedOutput = GetString(file, "output");
            if (string.IsNullOrWhiteSpace(reportedOutput))
            {
                throw new InvalidOperationException(CoreText.Get("SeConv_NoOutputPath"));
            }

            var resolvedReportedOutput = Path.GetFullPath(reportedOutput, workingDirectory);
            if (!string.Equals(
                    resolvedReportedOutput,
                    Path.GetFullPath(expectedOutputPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(CoreText.Get("SeConv_UnexpectedOutputPath"));
            }

            var warnings = new List<string>();
            AddMessages(root, "warnings", warnings);
            AddMessages(file, "warnings", warnings);
            return warnings.Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(CoreText.Get("SeConv_JsonParseFailed"), exception);
        }
    }

    private static void AddMessages(JsonElement parent, string propertyName, ICollection<string> messages)
    {
        if (!parent.TryGetProperty(propertyName, out var values)
            || values.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            var single = values.ToString();
            if (!string.IsNullOrWhiteSpace(single))
            {
                messages.Add(single);
            }

            return;
        }

        foreach (var value in values.EnumerateArray())
        {
            var message = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Add(message);
            }
        }
    }

    private static void ValidateOutputFile(string outputPath, SubtitleOutputFormat format)
    {
        var file = new FileInfo(outputPath);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidOperationException(CoreText.Get("SeConv_OutputNotCreated"));
        }

        var text = File.ReadAllText(outputPath);
        if (format == SubtitleOutputFormat.SubRip)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    text,
                    @"\d{2}:\d{2}:\d{2}[,.]\d{3}\s+-->\s+\d{2}:\d{2}:\d{2}[,.]\d{3}"))
            {
                throw new InvalidOperationException(CoreText.Get("SeConv_InvalidSrtTimecode"));
            }
        }
        else if (!text.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)
                 || !text.Contains("[Events]", StringComparison.OrdinalIgnoreCase)
                 || !text.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(CoreText.Get("SeConv_InvalidAssSections"));
        }
    }

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : -1;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SafeExtension(string path, string fallback)
    {
        var extension = Path.GetExtension(path);
        return extension.Length is > 1 and <= 10
               && extension.Skip(1).All(char.IsAsciiLetterOrDigit)
            ? extension.ToLowerInvariant()
            : fallback;
    }

    private static string OutputExtension(SubtitleOutputFormat format) =>
        format == SubtitleOutputFormat.SubRip ? ".srt" : ".ass";

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
            // Best-effort cleanup of operation-owned staging files.
        }
    }

    private static string BuildFailureMessage(string title, ProcessResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return CoreText.Get("Process_FailureWithExitCode", title, result.ExitCode, details.Trim());
    }
}
