using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Fonts;
using SubMuxBatch.Core.Media;
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
        bool? workspaceExistsWhenCompleted = null;
        var progress = new InlineProgress<JobProgress>(update =>
        {
            if (update.State is JobState.Succeeded or JobState.SucceededWithWarnings)
            {
                workspaceExistsWhenCompleted = Directory.EnumerateDirectories(_root, ".submuxbatch-*").Any();
            }
        });

        var result = await new BatchProcessor(runner).ProcessAsync(
            media,
            plan,
            new AppSettings { AttachAssStyleFonts = false },
            dependencies,
            progress);

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(2, runner.SeConvCalls.Count);
        Assert.Contains(runner.SeConvCalls, args => args.Contains("subrip"));
        Assert.Contains(runner.SeConvCalls, args => args.Contains("assa"));
        Assert.Contains(SubMuxMetadata.VersionTagName, runner.MuxedGlobalTagsText);
        Assert.Contains(SubMuxMetadata.ProcessedTagName, runner.MuxedGlobalTagsText);
        Assert.Contains(SubMuxMetadata.ProcessedValue, runner.MuxedGlobalTagsText);
        Assert.DoesNotContain(SubMuxMetadata.LegacyCommentTagName, runner.MuxedGlobalTagsText);
        Assert.True(File.Exists(mkv));
        Assert.False(workspaceExistsWhenCompleted);
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
            new AppSettings { OutputPrefix = string.Empty, AttachAssStyleFonts = false },
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
            new AppSettings { AttachAssStyleFonts = false },
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
                PlayResY = 1080,
                AttachAssStyleFonts = false
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
                PlayResY = 1080,
                AttachAssStyleFonts = false
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
            new AppSettings { OutputPrefix = "result_", AttachAssStyleFonts = false },
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
            new AppSettings { OutputPrefix = "result_", AttachAssStyleFonts = false },
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
        var settings = new AppSettings { OutputPrefix = "result_", AttachAssStyleFonts = false };

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubMuxGlobalTagFollowsSetting(bool addSubMuxTag)
    {
        var mkv = Path.Combine(_root, $"Tag-{addSubMuxTag}.mkv");
        var srt = Path.Combine(_root, $"Tag-{addSubMuxTag}.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var media = new MediaSet(new MediaKey(_root, $"Tag-{addSubMuxTag}"), mkv, null, srt, null);
        var runner = new FakeProcessRunner();

        var result = await new BatchProcessor(runner).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings
            {
                AttachAssStyleFonts = false,
                AddSubMuxTag = addSubMuxTag
            },
            CreateDependencies());

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.Equal(addSubMuxTag, runner.MuxedGlobalTagsText is not null);
    }

    [Fact]
    public async Task FinalOutputFileMustExistAndKeepItsVerifiedSize()
    {
        var valid = Path.Combine(_root, "valid-final.mkv");
        var empty = Path.Combine(_root, "empty-final.mkv");
        await File.WriteAllBytesAsync(valid, [1, 2, 3]);
        await File.WriteAllBytesAsync(empty, []);

        BatchProcessor.ValidateCommittedOutputFile(valid, 3);
        Assert.Throws<InvalidOperationException>(() =>
            BatchProcessor.ValidateCommittedOutputFile(empty, 3));
        Assert.Throws<InvalidOperationException>(() =>
            BatchProcessor.ValidateCommittedOutputFile(valid, 4));
        Assert.Throws<InvalidOperationException>(() =>
            BatchProcessor.ValidateCommittedOutputFile(Path.Combine(_root, "missing.mkv"), 3));
    }

    [Fact]
    public async Task MatchingAssFontIsAttachedAndJobSucceeds()
    {
        var mkv = Path.Combine(_root, "FontMatch.mkv");
        var srt = Path.Combine(_root, "FontMatch.srt");
        var font = Path.Combine(_root, "test-family-bold.otf");
        var alternateFont = Path.Combine(_root, "test-family-bold-alternate.otf");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        await File.WriteAllBytesAsync(font, [10, 20, 30, 40]);
        await File.WriteAllBytesAsync(alternateFont, [50, 60, 70, 80]);
        var media = new MediaSet(new MediaKey(_root, "FontMatch"), mkv, null, srt, null);
        var runner = new FakeProcessRunner();
        var resolver = new StaticFontResolver(
        [
            new FontAttachmentFile(font, "font/otf"),
            new FontAttachmentFile(alternateFont, "font/otf")
        ]);

        var result = await new BatchProcessor(runner, resolver).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings
            {
                OutputPrefix = "result_",
                AssStyleLine = AppSettings.DefaultAssStyleLine.Replace(
                    "맑은 고딕",
                    "Test Family",
                    StringComparison.Ordinal)
            },
            CreateDependencies());

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.Empty(result.Warnings);
        var muxArguments = Assert.Single(runner.MuxCalls);
        Assert.Contains("--attach-file", muxArguments);
        Assert.Contains(font, muxArguments);
        Assert.DoesNotContain(alternateFont, muxArguments);
        Assert.Contains("font/otf", muxArguments);
    }

    [Fact]
    public async Task MissingAssFontAddsWarningAndSkipsJobWithoutOutput()
    {
        var mkv = Path.Combine(_root, "FontMissing.mkv");
        var srt = Path.Combine(_root, "FontMissing.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var media = new MediaSet(new MediaKey(_root, "FontMissing"), mkv, null, srt, null);
        var runner = new FakeProcessRunner();

        var result = await new BatchProcessor(runner, new StaticFontResolver([])).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { OutputPrefix = "result_" },
            CreateDependencies());

        Assert.Equal(JobState.Skipped, result.State);
        Assert.Contains(result.Warnings, static warning => warning.Contains("Test Family") && warning.Contains("찾지 못했습니다"));
        Assert.Empty(runner.MuxCalls);
        Assert.Null(result.OutputPath);
        Assert.Contains("건너뜁니다", result.Error);
    }

    [Fact]
    public async Task FontAttachmentsAreContentDeduplicatedAndFilenameCollisionsAreRenamed()
    {
        var firstFolder = Path.Combine(_root, "first-font");
        var secondFolder = Path.Combine(_root, "second-font");
        var duplicateFolder = Path.Combine(_root, "duplicate-font");
        Directory.CreateDirectory(firstFolder);
        Directory.CreateDirectory(secondFolder);
        Directory.CreateDirectory(duplicateFolder);
        var first = Path.Combine(firstFolder, "Regular.ttf");
        var second = Path.Combine(secondFolder, "Regular.ttf");
        var duplicate = Path.Combine(duplicateFolder, "ZCopy.ttf");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(second, [4, 5, 6]);
        await File.WriteAllBytesAsync(duplicate, [1, 2, 3]);

        var result = await BatchProcessor.DeduplicateAndNameFontAttachmentsAsync(
            [
                new FontAttachmentFile(first, "font/ttf"),
                new FontAttachmentFile(second, "font/ttf"),
                new FontAttachmentFile(duplicate, "font/ttf")
            ],
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, static attachment => attachment.FileName == "Regular.ttf");
        Assert.Contains(result, static attachment =>
            attachment.FileName.StartsWith("Regular-", StringComparison.OrdinalIgnoreCase)
            && attachment.FileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase));
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class SkipProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default) =>
            throw new JobSkippedException("해당 작업은 건너뜁니다.");
    }

    private sealed class StaticFontResolver(IReadOnlyList<FontAttachmentFile> files) : IInstalledFontResolver
    {
        public IReadOnlyList<FontAttachmentFile> FindByFamilyName(string familyName) => files;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> SeConvCalls { get; } = [];
        public List<IReadOnlyList<string>> MuxCalls { get; } = [];
        public string? MuxedAssText { get; private set; }
        public string? MuxedGlobalTagsText { get; private set; }

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
                    File.WriteAllText(
                        output,
                        "[Script Info]\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,Test Family,40\n[Events]\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\\fs40}테스트\n");
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
                var inspectedPath = request.Arguments[1];
                var isOutput = File.Exists(inspectedPath)
                               && File.ReadAllBytes(inspectedPath).SequenceEqual(new byte[] { 7, 8, 9 });
                return Task.FromResult(new ProcessResult(0, isOutput ? CreateOutputJson() : SourceJson, string.Empty));
            }

            MuxCalls.Add(request.Arguments);
            _muxAttachments = ReadMuxAttachments(request.Arguments);
            var globalTagsIndex = request.Arguments.ToList().IndexOf("--global-tags");
            if (globalTagsIndex >= 0)
            {
                MuxedGlobalTagsText = File.ReadAllText(request.Arguments[globalTagsIndex + 1]);
            }
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

        private List<AttachedFont> _muxAttachments = [];

        private static List<AttachedFont> ReadMuxAttachments(IReadOnlyList<string> arguments)
        {
            var attachments = new List<AttachedFont>();
            for (var index = 0; index < arguments.Count; index++)
            {
                if (arguments[index] != "--attach-file" || index < 4)
                {
                    continue;
                }

                var path = arguments[index + 1];
                attachments.Add(new AttachedFont(
                    Path.GetFileName(path),
                    arguments[index - 3],
                    path));
            }

            return attachments;
        }

        private string CreateOutputJson()
        {
            var attachments = JsonSerializer.Serialize(_muxAttachments.Select(static font => new
            {
                file_name = font.FileName,
                content_type = font.MimeType,
                size = new FileInfo(font.Path).Length,
                properties = new { uid = font.FileName.GetHashCode(StringComparison.Ordinal) }
            }));
            return OutputJson.Replace("\"attachments\":[]", $"\"attachments\":{attachments}", StringComparison.Ordinal);
        }

        private sealed record AttachedFont(string FileName, string MimeType, string Path);

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
