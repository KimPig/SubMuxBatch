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
    [Theory]
    [InlineData(true, false, "ASS")]
    [InlineData(false, false, "SRT")]
    [InlineData(false, true, "SMI")]
    public void DeterminesAssSourceTagFromConversionPlan(bool existingAss, bool fromSmi, string expected)
    {
        var media = new MediaSet(
            new MediaKey(_root, "sample"),
            Path.Combine(_root, "sample.mkv"),
            existingAss ? Path.Combine(_root, "sample.ass") : null,
            existingAss || fromSmi ? null : Path.Combine(_root, "sample.srt"),
            fromSmi ? Path.Combine(_root, "sample.smi") : null);
        var plan = new ConversionPlan(
            true,
            existingAss ? AssSourceKind.Existing : AssSourceKind.ConvertFromSrt,
            fromSmi ? SrtSourceKind.ConvertFromSmi : existingAss ? SrtSourceKind.ConvertFromAss : SrtSourceKind.Existing,
            "test",
            []);

        Assert.Equal(expected, BatchProcessor.GetAssSourceTagValue(media, plan));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "SubMuxBatchPipelineTests", Guid.NewGuid().ToString("N"));

    public BatchProcessorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SmiOnlyRunsTwoEmbeddedConversionsAndCommitsVerifiedMkv()
    {
        var mkv = Path.Combine(_root, "Episode.mkv");
        var smi = Path.Combine(_root, "Episode.smi");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(
            smi,
            "<SAMI><BODY><SYNC Start=0><P>test<SYNC Start=1000><P>&nbsp;</BODY></SAMI>");

        var media = new MediaSet(new MediaKey(_root, "Episode"), mkv, null, null, smi);
        var plan = ConversionPlanFactory.Create(media);
        var runner = new FakeProcessRunner();
        var converter = new RecordingSubtitleConverter();
        var dependencies = new DependencyReport(
            new ToolDependency("MKVToolNix", "mkvmerge.exe", "fake-mkvmerge.exe", "test"));
        bool? workspaceExistsWhenCompleted = null;
        var progress = new InlineProgress<JobProgress>(update =>
        {
            if (update.State is JobState.Succeeded or JobState.SucceededWithWarnings)
            {
                workspaceExistsWhenCompleted = Directory.EnumerateDirectories(_root, ".submuxbatch-*").Any();
            }
        });

        var result = await new BatchProcessor(runner, subtitleConverter: converter).ProcessAsync(
            media,
            plan,
            new AppSettings { AttachAssStyleFonts = false },
            dependencies,
            progress);

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(2, converter.Calls.Count);
        Assert.Contains(converter.Calls, call => call.OutputFormat == SubtitleOutputFormat.SubRip);
        Assert.Contains(converter.Calls, call => call.OutputFormat == SubtitleOutputFormat.AdvancedSubStationAlpha);
        Assert.Contains(SubMuxMetadata.VersionTagName, runner.MuxedGlobalTagsText);
        Assert.Contains(SubMuxMetadata.ProcessedTagName, runner.MuxedGlobalTagsText);
        Assert.Contains(SubMuxMetadata.ProcessedValue, runner.MuxedGlobalTagsText);
        Assert.DoesNotContain(SubMuxMetadata.AssSourceTagName, runner.MuxedGlobalTagsText);
        Assert.DoesNotContain(SubMuxMetadata.LegacyCommentTagName, runner.MuxedGlobalTagsText);
        Assert.Contains("; SUBMUX_ASS_SOURCE=SMI", runner.MuxedAssText);
        var muxArguments = Assert.Single(runner.MuxCalls);
        var stagedOutput = muxArguments[muxArguments.ToList().IndexOf("-o") + 1];
        var workspaceDirectory = Assert.IsType<string>(Path.GetDirectoryName(stagedOutput));
        var workspaceName = Path.GetFileName(workspaceDirectory);
        Assert.StartsWith(WorkspaceNaming.CurrentPrefix, workspaceName, StringComparison.Ordinal);
        Assert.Equal(WorkspaceNaming.CurrentPrefix.Length + 12, workspaceName.Length);
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
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var media = new MediaSet(new MediaKey(_root, "Invalid"), mkv, null, srt, null);
        var dependencies = new DependencyReport(
            new ToolDependency("MKVToolNix", "mkvmerge.exe", "fake-mkvmerge.exe", "test"));

        var result = await new BatchProcessor(new FakeProcessRunner()).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { OutputPrefix = string.Empty, AttachAssStyleFonts = false },
            dependencies);

        Assert.Equal(JobState.Failed, result.State);
        Assert.Contains("접두사", result.Error);
    }

    [Fact]
    public async Task BackupFailureCompletesMuxWithWarningAndKeepsSource()
    {
        var video = Path.Combine(_root, "BackupWarning.mkv");
        var srt = Path.Combine(_root, "BackupWarning.srt");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var media = new MediaSet(new MediaKey(_root, "BackupWarning"), video, null, srt, null);

        var result = await new BatchProcessor(new BackupFailingRunner(video)).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings
            {
                AttachAssStyleFonts = false,
                BackupOriginalMetadata = true
            },
            CreateDependencies());

        Assert.Equal(JobState.SucceededWithWarnings, result.State);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.True(File.Exists(video));
        Assert.Contains(result.Warnings, static warning =>
            warning.Contains("원본 파일을 삭제하지 마세요", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FatalMuxFailureRollsBackOnlyBackupsCreatedByCurrentJob()
    {
        var video = Path.Combine(_root, "Rollback.mp4");
        var srt = Path.Combine(_root, "Rollback.srt");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var backupRoot = Path.Combine(_root, MetadataBackupService.BackupDirectoryName);
        var currentVideoDirectory = Path.Combine(backupRoot, "Rollback.mp4");
        var otherVideoDirectory = Path.Combine(backupRoot, "Other.mp4");
        Directory.CreateDirectory(currentVideoDirectory);
        Directory.CreateDirectory(otherVideoDirectory);
        var existingCurrentBackup = Path.Combine(currentVideoDirectory, "keep.txt");
        var otherBackup = Path.Combine(otherVideoDirectory, "metadata.json");
        await File.WriteAllTextAsync(existingCurrentBackup, "keep current");
        await File.WriteAllTextAsync(otherBackup, "keep other");
        var media = new MediaSet(new MediaKey(_root, "Rollback"), video, null, srt, null);

        var result = await new BatchProcessor(new FatalMp4ReadRunner()).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings
            {
                AttachAssStyleFonts = false,
                BackupOriginalMetadata = true
            },
            CreateDependencies());

        Assert.Equal(JobState.Failed, result.State);
        Assert.Null(result.OutputPath);
        Assert.Contains("원본 미디어 데이터를 끝까지 읽지 못했습니다", result.Error);
        Assert.Equal("keep current", await File.ReadAllTextAsync(existingCurrentBackup));
        Assert.Equal("keep other", await File.ReadAllTextAsync(otherBackup));
        Assert.False(File.Exists(Path.Combine(currentVideoDirectory, "metadata.json")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(currentVideoDirectory),
            static path => Path.GetFileName(path).StartsWith("metadata (", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".submuxbatch-*"));
    }

    [Fact]
    public async Task FatalMuxFailureRemovesNewEmptyBackupTree()
    {
        var video = Path.Combine(_root, "CleanRollback.mp4");
        var srt = Path.Combine(_root, "CleanRollback.srt");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var media = new MediaSet(new MediaKey(_root, "CleanRollback"), video, null, srt, null);

        var result = await new BatchProcessor(new FatalMp4ReadRunner()).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings
            {
                AttachAssStyleFonts = false,
                BackupOriginalMetadata = true
            },
            CreateDependencies());

        Assert.Equal(JobState.Failed, result.State);
        Assert.False(Directory.Exists(Path.Combine(
            _root,
            MetadataBackupService.BackupDirectoryName)));
    }

    [Fact]
    public async Task SuccessfulMuxCommitsNewBackupFiles()
    {
        var video = Path.Combine(_root, "BackupSuccess.mp4");
        var srt = Path.Combine(_root, "BackupSuccess.srt");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var media = new MediaSet(new MediaKey(_root, "BackupSuccess"), video, null, srt, null);

        var result = await new BatchProcessor(new FakeProcessRunner()).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings
            {
                AttachAssStyleFonts = false,
                BackupOriginalMetadata = true
            },
            CreateDependencies());

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.True(File.Exists(Path.Combine(
            _root,
            MetadataBackupService.BackupDirectoryName,
            "BackupSuccess.mp4",
            "metadata.json")));
    }

    [Fact]
    public async Task AssCommentsStayInAssTrackButAreExcludedFromGeneratedSrt()
    {
        var video = Path.Combine(_root, "Comments.mkv");
        var ass = Path.Combine(_root, "Comments.ass");
        const string sourceAss = "[Script Info]\n[V4+ Styles]\n[Events]\n"
                                 + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                                 + "Comment: 0,3:08:15.00,0:20:22.45,Default,,0,0,0,,Editor note\n"
                                 + "Dialogue: 0,0:00:01.00,0:00:04.00,Default,,0,0,0,,Visible subtitle\n";
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        await File.WriteAllTextAsync(ass, sourceAss);
        var media = new MediaSet(new MediaKey(_root, "Comments"), video, ass, null, null);
        var runner = new FakeProcessRunner();
        var converter = new RecordingSubtitleConverter();

        var result = await new BatchProcessor(runner, subtitleConverter: converter).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { AttachAssStyleFonts = false },
            CreateDependencies());

        Assert.Equal(JobState.Succeeded, result.State);
        var assConversion = Assert.Single(
            converter.Calls,
            call => call.OutputFormat == SubtitleOutputFormat.SubRip);
        Assert.DoesNotContain("Comment:", assConversion.InputText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Editor note", assConversion.InputText);
        Assert.Contains("Visible subtitle", assConversion.InputText);
        Assert.Contains("Editor note", runner.MuxedAssText);
        Assert.Equal(sourceAss, await File.ReadAllTextAsync(ass));
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
            new ToolDependency("MKVToolNix", "mkvmerge.exe", "fake-mkvmerge.exe", "test"));

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

        Assert.True(result.State == JobState.Succeeded, result.Error);
        Assert.NotNull(runner.MuxedAssText);
        Assert.Contains(@"\fs40", runner.MuxedAssText);
        Assert.Contains(@"\pos(320,72)", runner.MuxedAssText);
        Assert.Contains("테스트", runner.MuxedAssText);
    }

    [Fact]
    public async Task NegativeSrtTimestampIsClampedForBothSubtitleTracksAndAddsWarning()
    {
        var mkv = Path.Combine(_root, "NegativeTimestamp.mkv");
        var srt = Path.Combine(_root, "NegativeTimestamp.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        const string sourceText = "1\n-00:00:02,000 --> 00:00:05,000\nTest\n";
        await File.WriteAllTextAsync(srt, sourceText);
        var media = new MediaSet(new MediaKey(_root, "NegativeTimestamp"), mkv, null, srt, null);
        var runner = new FakeProcessRunner();
        var converter = new RecordingSubtitleConverter();

        var result = await new BatchProcessor(runner, subtitleConverter: converter).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { AttachAssStyleFonts = false },
            CreateDependencies());

        Assert.Equal(JobState.SucceededWithWarnings, result.State);
        Assert.Contains(result.Warnings, static warning =>
            warning.Contains("음수 타임스탬프", StringComparison.Ordinal)
            && warning.Contains("00:00:00,000 --> 00:00:05,000", StringComparison.Ordinal));
        var assConversion = Assert.Single(
            converter.Calls,
            call => call.OutputFormat == SubtitleOutputFormat.AdvancedSubStationAlpha);
        Assert.Contains("00:00:00,000 --> 00:00:05,000", assConversion.InputText);
        Assert.Contains("00:00:00,000 --> 00:00:05,000", runner.MuxedSrtText);
        Assert.Equal(sourceText, await File.ReadAllTextAsync(srt));
    }

    [Fact]
    public async Task NegativeSrtMillisecondComponentIsHandledBeforeMuxing()
    {
        var mkv = Path.Combine(_root, "NegativeMilliseconds.mkv");
        var srt = Path.Combine(_root, "NegativeMilliseconds.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        const string sourceText = "5\n"
                                  + "00:00:-3,-900 --> 00:00:00,-140\n"
                                  + "등장인물들은 모두 18세 이상입니다\n"
                                  + "5\n"
                                  + "00:00:00,-90 --> 00:00:03,340\n"
                                  + "가져가 주세요\n"
                                  + "00:00:09,370 --> 00:00:13,280\n"
                                  + "번호 없는 자막\n";
        await File.WriteAllTextAsync(srt, sourceText);
        var media = new MediaSet(new MediaKey(_root, "NegativeMilliseconds"), mkv, null, srt, null);
        var runner = new FakeProcessRunner();

        var result = await new BatchProcessor(runner).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { AttachAssStyleFonts = false },
            CreateDependencies());

        Assert.Equal(JobState.SucceededWithWarnings, result.State);
        Assert.Equal(2, result.Warnings.Count(static warning =>
            warning.Contains("음수 타임스탬프", StringComparison.Ordinal)
            || warning.Contains("영상 시작 전에 끝나", StringComparison.Ordinal)));
        Assert.DoesNotContain("등장인물들은", runner.MuxedSrtText);
        Assert.DoesNotContain(",-", runner.MuxedSrtText);
        Assert.DoesNotContain("00:00:-", runner.MuxedSrtText);
        Assert.Contains("1\r\n00:00:00,000 --> 00:00:03,340", runner.MuxedSrtText);
        Assert.Contains("2\r\n00:00:09,370 --> 00:00:13,280", runner.MuxedSrtText);
        Assert.Equal(sourceText, await File.ReadAllTextAsync(srt));
    }

    [Fact]
    public async Task SkipsJobWhenEverySrtCueEndsBeforeTheVideoStarts()
    {
        var mkv = Path.Combine(_root, "OnlyPreroll.mkv");
        var srt = Path.Combine(_root, "OnlyPreroll.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        const string sourceText = "9\n00:00:-3,-900 --> 00:00:00,-140\nIntro\n";
        await File.WriteAllTextAsync(srt, sourceText);
        var media = new MediaSet(new MediaKey(_root, "OnlyPreroll"), mkv, null, srt, null);

        var result = await new BatchProcessor(new FakeProcessRunner()).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { AttachAssStyleFonts = false },
            CreateDependencies());

        Assert.Equal(JobState.Skipped, result.State);
        Assert.Null(result.OutputPath);
        Assert.Contains("유효한 자막 항목이 남지 않아", result.Error);
        Assert.Contains(result.Warnings, static warning => warning.Contains("영상 시작 전에 끝나", StringComparison.Ordinal));
        Assert.Equal(sourceText, await File.ReadAllTextAsync(srt));
    }

    [Fact]
    public async Task NegativeAssTimestampIsClampedInMuxedCopyAndAddsWarning()
    {
        var mkv = Path.Combine(_root, "NegativeAss.mkv");
        var ass = Path.Combine(_root, "NegativeAss.ass");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        const string sourceText = "[Events]\n"
                                  + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                                  + "Dialogue: 0,0:00:-01.00,0:00:05.00,Default,,0,0,0,,Test\n";
        await File.WriteAllTextAsync(ass, sourceText);
        var media = new MediaSet(new MediaKey(_root, "NegativeAss"), mkv, ass, null, null);
        var runner = new FakeProcessRunner();
        var converter = new RecordingSubtitleConverter();

        var result = await new BatchProcessor(runner, subtitleConverter: converter).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { AttachAssStyleFonts = false },
            CreateDependencies());

        Assert.Equal(JobState.SucceededWithWarnings, result.State);
        Assert.Contains(result.Warnings, static warning =>
            warning.Contains("ASS", StringComparison.Ordinal)
            && warning.Contains("음수 타임스탬프", StringComparison.Ordinal));
        Assert.Contains("0:00:00.00,0:00:05.00", runner.MuxedAssText);
        var srtConversion = Assert.Single(
            converter.Calls,
            call => call.OutputFormat == SubtitleOutputFormat.SubRip);
        Assert.Contains("0:00:00.00,0:00:05.00", srtConversion.InputText);
        Assert.Equal(sourceText, await File.ReadAllTextAsync(ass));
    }

    [Fact]
    public async Task GeneratedAssIsValidatedAgainBeforeMuxing()
    {
        var mkv = Path.Combine(_root, "GeneratedAssValidation.mkv");
        var srt = Path.Combine(_root, "GeneratedAssValidation.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:03,990\nTest\n");
        var media = new MediaSet(new MediaKey(_root, "GeneratedAssValidation"), mkv, null, srt, null);
        const string generatedAss = "[Script Info]\n"
                                    + "[V4+ Styles]\n"
                                    + "Format: Name, Fontname, Fontsize\n"
                                    + "Style: Default,Test Family,40\n"
                                    + "[Events]\n"
                                    + "Dialogue: 0,0:00:-04.00,0:00:03.99,Default,,0,0,0,,Test\n";
        var runner = new FakeProcessRunner();

        var result = await new BatchProcessor(
            runner,
            subtitleConverter: new StaticAssSubtitleConverter(generatedAss)).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { AttachAssStyleFonts = false },
            CreateDependencies());

        Assert.Equal(JobState.SucceededWithWarnings, result.State);
        Assert.Contains(result.Warnings, static warning =>
            warning.Contains("ASS", StringComparison.Ordinal)
            && warning.Contains("음수 타임스탬프", StringComparison.Ordinal));
        Assert.DoesNotContain("0:00:-04.00", runner.MuxedAssText);
        Assert.Contains("0:00:00.00,0:00:03.99", runner.MuxedAssText);
    }

    [Fact]
    public async Task NegativeSmiTimestampIsClampedBeforeConversionAndAddsWarning()
    {
        var mkv = Path.Combine(_root, "NegativeSmi.mkv");
        var smi = Path.Combine(_root, "NegativeSmi.smi");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        const string sourceText = "<SAMI>\n<BODY>\n"
                                  + "<SYNC Start=-1000><P>Test\n"
                                  + "<SYNC Start=5000><P>&nbsp;\n"
                                  + "</BODY>\n</SAMI>\n";
        await File.WriteAllTextAsync(smi, sourceText);
        var media = new MediaSet(new MediaKey(_root, "NegativeSmi"), mkv, null, null, smi);
        var runner = new FakeProcessRunner();
        var converter = new RecordingSubtitleConverter();

        var result = await new BatchProcessor(runner, subtitleConverter: converter).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { AttachAssStyleFonts = false },
            CreateDependencies());

        Assert.Equal(JobState.SucceededWithWarnings, result.State);
        Assert.Contains(result.Warnings, static warning =>
            warning.Contains("SMI", StringComparison.Ordinal)
            && warning.Contains("음수 타임스탬프", StringComparison.Ordinal));
        var smiConversion = Assert.Single(
            converter.Calls,
            call => call.InputExtension.Equals(".smi", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("<SYNC Start=0><P>Test", smiConversion.InputText);
        Assert.Equal(sourceText, await File.ReadAllTextAsync(smi));
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
        var converter = new RecordingSubtitleConverter();

        var result = await new BatchProcessor(runner, subtitleConverter: converter).ProcessAsync(
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
        var assCall = Assert.Single(
            converter.Calls,
            call => call.OutputFormat == SubtitleOutputFormat.AdvancedSubStationAlpha);
        Assert.Equal(1920, assCall.PlayResX);
        Assert.Equal(1080, assCall.PlayResY);
        Assert.Equal(useCustomAssStyle, assCall.StyleText is not null);
    }

    [Fact]
    public async Task ExistingOutputsUseNextNumberedNameWithoutOverwritingFiles()
    {
        var mkv = Path.Combine(_root, "Collision.mkv");
        var srt = Path.Combine(_root, "Collision.srt");
        var existingBase = Path.Combine(_root, "result_Collision.mkv");
        var existingNumbered = Path.Combine(_root, "result_Collision (1).mkv");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
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
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
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
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");

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
        Assert.Equal(addSubMuxTag, runner.MuxedAssText?.Contains("; SUBMUX_ASS_SOURCE=SRT", StringComparison.Ordinal) == true);
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
                    "SubMux Sans",
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
    public async Task BundledSubMuxSansIsUsedOnlyWhenNoInstalledMatchExists()
    {
        var mkv = Path.Combine(_root, "BundledFont.mkv");
        var srt = Path.Combine(_root, "BundledFont.srt");
        await File.WriteAllBytesAsync(mkv, [1, 2, 3]);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
        var media = new MediaSet(new MediaKey(_root, "BundledFont"), mkv, null, srt, null);
        var runner = new FakeProcessRunner();

        var result = await new BatchProcessor(runner, new StaticFontResolver([])).ProcessAsync(
            media,
            ConversionPlanFactory.Create(media),
            new AppSettings { OutputPrefix = "result_" },
            CreateDependencies());

        Assert.Equal(JobState.Succeeded, result.State);
        Assert.Empty(result.Warnings);
        var arguments = Assert.Single(runner.MuxCalls);
        var attachmentIndex = arguments.ToList().IndexOf("--attach-file");
        Assert.True(attachmentIndex >= 0);
        Assert.EndsWith("SubMuxSans-Medium.otf", arguments[attachmentIndex + 1], StringComparison.OrdinalIgnoreCase);
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
            new AppSettings
            {
                OutputPrefix = "result_",
                AssStyleLine = AppSettings.DefaultAssStyleLine.Replace(
                    "SubMux Sans",
                    "Definitely Missing Font",
                    StringComparison.Ordinal)
            },
            CreateDependencies());

        Assert.Equal(JobState.Skipped, result.State);
        Assert.Contains(result.Warnings, static warning => warning.Contains("Definitely Missing Font") && warning.Contains("찾지 못했습니다"));
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
        new ToolDependency("MKVToolNix", "mkvmerge.exe", "fake-mkvmerge.exe", "test"));

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

    private sealed class BackupFailingRunner(string sourcePath) : IProcessRunner
    {
        private readonly FakeProcessRunner _inner = new();

        public async Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            if (request.Arguments.Count > 1
                && request.Arguments[0] == "-J"
                && string.Equals(request.Arguments[1], sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return new ProcessResult(0, SourceMatroskaJson, string.Empty);
            }

            return await _inner.RunAsync(request, onOutput, cancellationToken);
        }

        private const string SourceMatroskaJson = """
        {"container":{"type":"Matroska"},"tracks":[
          {"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC","default_track":true,"forced_track":false}},
          {"type":"audio","properties":{"codec_id":"A_OPUS","default_track":true,"forced_track":false}}
        ],"attachments":[],"chapters":[]}
        """;
    }

    private sealed class FatalMp4ReadRunner : IProcessRunner
    {
        private readonly FakeProcessRunner _inner = new();

        public async Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.RunAsync(request, onOutput, cancellationToken);
            if (!request.Arguments.Contains("--gui-mode"))
            {
                return result;
            }

            const string warning = "Quicktime/MP4 reader: Could not read 847 bytes at position 9335545265 for chunk number 219594/226031. Aborting.";
            var output = $"#GUI#progress 100%{Environment.NewLine}#GUI#warning {warning}{Environment.NewLine}";
            onOutput?.Invoke($"#GUI#warning {warning}");
            return new ProcessResult(1, output, string.Empty);
        }
    }

    private sealed class StaticFontResolver(IReadOnlyList<FontAttachmentFile> files) : IInstalledFontResolver
    {
        public IReadOnlyList<FontAttachmentFile> FindByFamilyName(string familyName) => files;
    }

    private sealed record SubtitleConversionCall(
        SubtitleOutputFormat OutputFormat,
        string InputExtension,
        string InputText,
        string? StyleText,
        int PlayResX,
        int PlayResY);

    private sealed class RecordingSubtitleConverter : ISubtitleConverter
    {
        private readonly LibSeSubtitleConverter _inner = new();

        public List<SubtitleConversionCall> Calls { get; } = [];

        public async Task<SubtitleConversionResult> ConvertAsync(
            string inputPath,
            string outputPath,
            SubtitleOutputFormat outputFormat,
            string? assStylePath,
            int playResX,
            int playResY,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new SubtitleConversionCall(
                outputFormat,
                Path.GetExtension(inputPath),
                await File.ReadAllTextAsync(inputPath, cancellationToken),
                assStylePath is null
                    ? null
                    : await File.ReadAllTextAsync(assStylePath, cancellationToken),
                playResX,
                playResY));
            return await _inner.ConvertAsync(
                inputPath,
                outputPath,
                outputFormat,
                assStylePath,
                playResX,
                playResY,
                onOutput,
                cancellationToken);
        }
    }

    private sealed class StaticAssSubtitleConverter(string generatedAss) : ISubtitleConverter
    {
        public async Task<SubtitleConversionResult> ConvertAsync(
            string inputPath,
            string outputPath,
            SubtitleOutputFormat outputFormat,
            string? assStylePath,
            int playResX,
            int playResY,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SubtitleOutputFormat.AdvancedSubStationAlpha, outputFormat);
            await File.WriteAllTextAsync(outputPath, generatedAss, cancellationToken);
            return new SubtitleConversionResult([], "test", "utf-8", 1);
        }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> MuxCalls { get; } = [];
        public string? MuxedAssText { get; private set; }
        public string? MuxedSrtText { get; private set; }
        public string? MuxedGlobalTagsText { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            var srtInput = request.Arguments.FirstOrDefault(static argument =>
                Path.GetExtension(argument).Equals(".srt", StringComparison.OrdinalIgnoreCase));
            if (srtInput is not null)
            {
                MuxedSrtText = File.ReadAllText(srtInput);
            }

            File.WriteAllBytes(request.Arguments[outputIndex], [7, 8, 9]);
            onOutput?.Invoke("#GUI#progress 100%");
            return Task.FromResult(new ProcessResult(0, "#GUI#progress 100%", string.Empty));
        }

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
