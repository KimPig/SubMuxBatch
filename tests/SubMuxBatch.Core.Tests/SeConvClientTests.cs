using System.Text.Json;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class SeConvClientTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "[Group] SeConvClientTests",
        Guid.NewGuid().ToString("N"));

    public SeConvClientTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task StagesUnsafePathsAndReturnsToolWarnings()
    {
        var input = Path.Combine(_root, "[Release Group] Show.srt");
        var output = Path.Combine(_root, "result.srt");
        await File.WriteAllTextAsync(input, "1\n00:00:00,000 --> 00:00:01,000\n테스트\n");
        var runner = new WarningRunner();

        var result = await new SeConvClient("seconv.exe", runner).ConvertAsync(
            input,
            output,
            SubtitleOutputFormat.SubRip,
            null,
            1280,
            720);

        Assert.True(runner.ReceivedOnlySafeArguments);
        Assert.Equal(["root warning", "file warning"], result.Warnings);
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_root, "seconv-*-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AssConversionAlwaysPassesResolutionAndOptionallyStagesStyle(bool useStyle)
    {
        var input = Path.Combine(_root, "source.srt");
        var output = Path.Combine(_root, useStyle ? "styled.ass" : "default.ass");
        var style = Path.Combine(_root, "style.ass");
        await File.WriteAllTextAsync(input, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        if (useStyle)
        {
            await File.WriteAllTextAsync(style, AssStyleTemplateWriter.Create(new AppSettings()));
        }

        var runner = new AssRunner();
        await new SeConvClient("seconv.exe", runner).ConvertAsync(
            input,
            output,
            SubtitleOutputFormat.AdvancedSubStationAlpha,
            useStyle ? style : null,
            1920,
            1080);

        Assert.Contains("--resolution:1920x1080", runner.Arguments);
        Assert.Equal(
            useStyle,
            runner.Arguments.Any(static argument =>
                argument.StartsWith("--assa-style-file:", StringComparison.Ordinal)));
        Assert.Equal(useStyle, runner.StagedStyleText is not null);
        if (useStyle)
        {
            Assert.Contains(AppSettings.DefaultAssStyleLine, runner.StagedStyleText);
        }

        Assert.True(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_root, "seconv-*-*"));
    }

    [Fact]
    public async Task AssConversionRejectsAProvidedMissingStyleFile()
    {
        var input = Path.Combine(_root, "missing-style.srt");
        var output = Path.Combine(_root, "missing-style.ass");
        await File.WriteAllTextAsync(input, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SeConvClient("seconv.exe", new AssRunner()).ConvertAsync(
                input,
                output,
                SubtitleOutputFormat.AdvancedSubStationAlpha,
                Path.Combine(_root, "does-not-exist.ass"),
                1920,
                1080));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_root, "seconv-*-*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class WarningRunner : IProcessRunner
    {
        public bool ReceivedOnlySafeArguments { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedOnlySafeArguments = request.Arguments.All(static argument =>
                !argument.Contains('[') && !argument.Contains(']'));
            var outputName = request.Arguments
                .Single(static argument => argument.StartsWith("--output-filename:", StringComparison.Ordinal))
                ["--output-filename:".Length..];
            File.WriteAllText(
                Path.Combine(request.WorkingDirectory!, outputName),
                "1\n00:00:00,000 --> 00:00:01,000\n테스트\n");

            var json = JsonSerializer.Serialize(new
            {
                success = true,
                totalFiles = 1,
                successfulFiles = 1,
                failedFiles = 0,
                files = new[]
                {
                    new
                    {
                        input = request.Arguments[0],
                        output = $@".\{outputName}",
                        success = true,
                        error = (string?)null,
                        warnings = new[] { "file warning" }
                    }
                },
                errors = Array.Empty<string>(),
                warnings = new[] { "root warning" }
            });
            return Task.FromResult(new ProcessResult(0, json, string.Empty));
        }
    }

    private sealed class AssRunner : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string? StagedStyleText { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Arguments = request.Arguments;
            var outputName = request.Arguments
                .Single(static argument => argument.StartsWith("--output-filename:", StringComparison.Ordinal))
                ["--output-filename:".Length..];
            var styleArgument = request.Arguments.FirstOrDefault(static argument =>
                argument.StartsWith("--assa-style-file:", StringComparison.Ordinal));
            if (styleArgument is not null)
            {
                var styleName = styleArgument["--assa-style-file:".Length..];
                StagedStyleText = File.ReadAllText(Path.Combine(request.WorkingDirectory!, styleName));
            }

            File.WriteAllText(
                Path.Combine(request.WorkingDirectory!, outputName),
                "[Script Info]\n[V4+ Styles]\n[Events]\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Test\n");

            var json = JsonSerializer.Serialize(new
            {
                success = true,
                totalFiles = 1,
                successfulFiles = 1,
                failedFiles = 0,
                files = new[]
                {
                    new
                    {
                        input = request.Arguments[0],
                        output = $@".\{outputName}",
                        success = true,
                        error = (string?)null,
                        warnings = (string[]?)null
                    }
                },
                errors = Array.Empty<string>(),
                warnings = Array.Empty<string>()
            });
            return Task.FromResult(new ProcessResult(0, json, string.Empty));
        }
    }
}
