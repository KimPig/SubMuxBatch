using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Planning;
using SubMuxBatch.Core.Processing;
using System.Text.Json;

namespace SubMuxBatch.Core.Tests;

public sealed class BatchProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SubMuxBatchPipelineTests", Guid.NewGuid().ToString("N"));

    public BatchProcessorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SmiOnlyRunsTwoConversionsAndCommitsVerifiedMkv()
    {
        var mkv = Path.Combine(_root, "Episode.mkv");
        var smi = Path.Combine(_root, "Episode.smi");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(smi, "<SAMI>test</SAMI>");

        var media = new MediaSet(new MediaKey(_root, "Episode"), mkv, null, null, smi);
        var plan = ConversionPlanFactory.Create(media);
        var runner = new FakeProcessRunner();
        var dependencies = new DependencyReport(
            new ToolDependency("MKVToolNix", "mkvmerge.exe", "fake-mkvmerge.exe", "test"),
            new ToolDependency("Subtitle Edit seconv", "seconv.exe", "fake-seconv.exe", "test"));

        var result = await new BatchProcessor(runner).ProcessAsync(
            media,
            plan,
            new AppSettings(),
            dependencies);

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(2, runner.SeConvCalls.Count);
        Assert.Contains(runner.SeConvCalls, args => args.Contains("subrip"));
        Assert.Contains(runner.SeConvCalls, args => args.Contains("assa"));
        Assert.True(File.Exists(mkv));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".submuxbatch-*"));
    }

    [Fact]
    public async Task InvalidSettingsReturnFailedResultInsteadOfEscaping()
    {
        var mkv = Path.Combine(_root, "Invalid.mkv");
        var srt = Path.Combine(_root, "Invalid.srt");
        await File.WriteAllBytesAsync(mkv, [1]);
        await File.WriteAllTextAsync(srt, "test");
        var media = new MediaSet(new MediaKey(_root, "Invalid"), mkv, null, srt, null);
        var dependencies = new DependencyReport(
            new ToolDependency("MKVToolNix", "mkvmerge.exe", "fake-mkvmerge.exe", "test"),
            new ToolDependency("Subtitle Edit seconv", "seconv.exe", "fake-seconv.exe", "test"));

        var result = await new BatchProcessor(new FakeProcessRunner()).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { OutputPrefix = string.Empty },
            dependencies);

        Assert.Equal(JobState.Failed, result.State);
        Assert.Contains("접두사", result.Error);
    }

    [Fact]
    public async Task SkipSignalReturnsSkippedResult()
    {
        var video = Path.Combine(_root, "Skipped.mkv");
        var ass = Path.Combine(_root, "Skipped.ass");
        var srt = Path.Combine(_root, "Skipped.srt");
        await File.WriteAllBytesAsync(video, [1]);
        await File.WriteAllTextAsync(ass, "[Script Info]\n[V4+ Styles]\n[Events]");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var media = new MediaSet(new MediaKey(_root, "Skipped"), video, ass, srt, null);

        var result = await new BatchProcessor(new SkipProcessRunner()).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings(),
            CreateDependencies());

        Assert.Equal(JobState.Skipped, result.State);
        Assert.Null(result.OutputPath);
        Assert.Contains("해당 작업은 건너뜁니다.", result.Error);
        Assert.Empty(Directory.EnumerateDirectories(_root, ".submuxbatch-*"));
    }

    [Fact]
    public async Task SrtToAssPipelineRestoresInlineGeometryWithoutScaling()
    {
        var mkv = Path.Combine(_root, "Scaled.mkv");
        var srt = Path.Combine(_root, "Scaled.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(
            srt,
            "1\n00:00:00,000 --> 00:00:01,000\n{\\pos(320,72)}<font size=\"40\">테스트</font>\n");
        var media = new MediaSet(new MediaKey(_root, "Scaled"), mkv, null, srt, null);
        var runner = new FakeProcessRunner();
        var dependencies = new DependencyReport(
            new ToolDependency("MKVToolNix", "mkvmerge.exe", "fake-mkvmerge.exe", "test"),
            new ToolDependency("Subtitle Edit seconv", "seconv.exe", "fake-seconv.exe", "test"));

        var result = await new BatchProcessor(runner).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings
            {
                PlayResX = 1920,
                PlayResY = 1080
            },
            dependencies);

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.NotNull(runner.MuxedAssText);
        Assert.Contains(@"{\fs40\pos(320,72)}테스트", runner.MuxedAssText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SrtToAssPassesStyleOnlyWhenEnabled(bool useCustomAssStyle)
    {
        var mkv = Path.Combine(_root, "StyleToggle.mkv");
        var srt = Path.Combine(_root, "StyleToggle.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var media = new MediaSet(new MediaKey(_root, "StyleToggle"), mkv, null, srt, null);
        var runner = new FakeProcessRunner();

        var result = await new BatchProcessor(runner).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings
            {
                UseCustomAssStyle = useCustomAssStyle,
                PlayResX = 1920,
                PlayResY = 1080
            },
            CreateDependencies());

        Assert.Equal(JobState.Succeeded, result.State);
        var assCall = Assert.Single(runner.SeConvCalls, static args => args.Contains("assa"));
        Assert.Contains("--resolution:1920x1080", assCall);
        Assert.Equal(
            useCustomAssStyle,
            assCall.Any(static argument => argument.StartsWith("--assa-style-file:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExistingOutputsUseNextNumberedNameWithoutOverwritingFiles()
    {
        var mkv = Path.Combine(_root, "Collision.mkv");
        var srt = Path.Combine(_root, "Collision.srt");
        var existingBase = Path.Combine(_root, "result_Collision.mkv");
        var existingNumbered = Path.Combine(_root, "result_Collision (1).mkv");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "test");
        await File.WriteAllBytesAsync(existingBase, [10, 11]);
        await File.WriteAllBytesAsync(existingNumbered, [20, 21]);

        var media = new MediaSet(new MediaKey(_root, "Collision"), mkv, null, srt, null);
        var result = await new BatchProcessor(new FakeProcessRunner()).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { OutputPrefix = "result_" },
            CreateDependencies());

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.Equal(Path.Combine(_root, "result_Collision (2).mkv"), result.OutputPath);
        Assert.Equal(new byte[] { 10, 11 }, await File.ReadAllBytesAsync(existingBase));
        Assert.Equal(new byte[] { 20, 21 }, await File.ReadAllBytesAsync(existingNumbered));
        Assert.True(File.Exists(result.OutputPath));
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".mp4")]
    [InlineData(".m4v")]
    [InlineData(".mov")]
    [InlineData(".avi")]
    [InlineData(".ts")]
    [InlineData(".mts")]
    [InlineData(".m2ts")]
    [InlineData(".webm")]
    public async Task SupportedVideoIsPassedToMkvmergeAndOutputIsAlwaysMkv(string extension)
    {
        var stem = "Input_" + extension.TrimStart('.');
        var video = Path.Combine(_root, stem + extension);
        var srt = Path.Combine(_root, stem + ".srt");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "test");
        var media = new MediaSet(new MediaKey(_root, stem), video, null, srt, null);
        var runner = new FakeProcessRunner();

        var result = await new BatchProcessor(runner).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { OutputPrefix = "result_" },
            CreateDependencies());

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.Equal(Path.Combine(_root, $"result_{stem}.mkv"), result.OutputPath);
        Assert.Contains(runner.MuxCalls, arguments => arguments.Contains(video));
        Assert.True(File.Exists(video));
    }
    [Fact]
    public async Task ConcurrentJobsCommitToDifferentNamesWithoutOverwriting()
    {
        var mkv = Path.Combine(_root, "Concurrent.mkv");
        var srt = Path.Combine(_root, "Concurrent.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "test");

        var media = new MediaSet(new MediaKey(_root, "Concurrent"), mkv, null, srt, null);
        var plan = ConversionPlanFactory.Create(media);
        var settings = new AppSettings { OutputPrefix = "result_" };

        var results = await Task.WhenAll(
            new BatchProcessor(new FakeProcessRunner()).ProcessAsync(
                media,
                plan,
                settings,
                CreateDependencies()),
            new BatchProcessor(new FakeProcessRunner()).ProcessAsync(
                media,
                plan,
                settings,
                CreateDependencies()));

        Assert.All(results, result => Assert.Equal(JobState.Succeeded, result.State));
        Assert.Equal(2, results.Select(static result => result.OutputPath).Distinct().Count());
        Assert.Contains(Path.Combine(_root, "result_Concurrent.mkv"), results.Select(static result => result.OutputPath));
        Assert.Contains(Path.Combine(_root, "result_Concurrent (1).mkv"), results.Select(static result => result.OutputPath));
        Assert.All(results, result => Assert.True(File.Exists(result.OutputPath)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static DependencyReport CreateDependencies() => new(
        new ToolDependency("MKVToolNix", "mkvmerge.exe", "fake-mkvmerge.exe", "test"),
        new ToolDependency("Subtitle Edit seconv", "seconv.exe", "fake-seconv.exe", "test"));

    private sealed class SkipProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default) =>
            throw new JobSkippedException("해당 작업은 건너뜁니다.");
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> SeConvCalls { get; } = [];
        public List<IReadOnlyList<string>> MuxCalls { get; } = [];
        public string? MuxedAssText { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FileName.Contains("seconv", StringComparison.OrdinalIgnoreCase))
            {
                SeConvCalls.Add(request.Arguments);
                var outputFolder = ValueOf(request.Arguments, "--output-folder:");
                var outputName = ValueOf(request.Arguments, "--output-filename:");
                var workingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory;
                var output = Path.GetFullPath(Path.Combine(workingDirectory, outputFolder, outputName));
                if (request.Arguments.Contains("subrip"))
                {
                    File.WriteAllText(output, "1\n00:00:00,000 --> 00:00:01,000\n테스트\n");
                }
                else
                {
                    File.WriteAllText(output, "[Script Info]\n[V4+ Styles]\n[Events]\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\\fs40}테스트\n");
                }

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

            if (request.Arguments[0] == "-J")
            {
                var isOutput = request.Arguments[1].Contains("output.partial", StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(new ProcessResult(0, isOutput ? OutputJson : SourceJson, string.Empty));
            }

            MuxCalls.Add(request.Arguments);
            var outputIndex = request.Arguments.ToList().IndexOf("-o") + 1;
            var assInput = request.Arguments.FirstOrDefault(static argument =>
                Path.GetExtension(argument).Equals(".ass", StringComparison.OrdinalIgnoreCase));
            if (assInput is not null)
            {
                MuxedAssText = File.ReadAllText(assInput);
            }

            File.WriteAllBytes(request.Arguments[outputIndex], [7, 8, 9]);
            onOutput?.Invoke("#GUI#progress 100%");
            return Task.FromResult(new ProcessResult(0, "#GUI#progress 100%", string.Empty));
        }

        private static string ValueOf(IReadOnlyList<string> arguments, string prefix) =>
            arguments.First(argument => argument.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

        private const string SourceJson = """
        {"tracks":[
          {"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC","default_track":true,"forced_track":false}},
          {"type":"audio","properties":{"codec_id":"A_OPUS","default_track":true,"forced_track":false}}
        ],"attachments":[],"chapters":[]}
        """;

        private const string OutputJson = """
        {"tracks":[
          {"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC","default_track":true,"forced_track":false}},
          {"type":"audio","properties":{"codec_id":"A_OPUS","default_track":true,"forced_track":false}},
          {"type":"subtitles","properties":{"codec_id":"S_TEXT/ASS","default_track":true,"forced_track":false,"language":"kor","language_ietf":"ko"}},
          {"type":"subtitles","properties":{"codec_id":"S_TEXT/UTF8","default_track":false,"forced_track":false,"language":"kor","language_ietf":"ko"}}
        ],"attachments":[],"chapters":[]}
        """;
    }
}
