using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Media;
using SubMuxBatch.Core.Processing;

namespace SubMuxBatch.Core.Tests;

public sealed class MaintenanceIntegrationTests
{
    [Fact]
    public async Task MaintainsTaggedGeneratedAssWithoutReencodingVideo()
    {
        var mkvMergePath = @"C:\Program Files\MKVToolNix\mkvmerge.exe";
        var ffmpegPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "dependencies", "ffmpeg", "win-x64", "ffmpeg.exe"));
        if (!File.Exists(mkvMergePath) || !File.Exists(ffmpegPath)) return;

        var root = Path.Combine(Path.GetTempPath(), $"SubMuxBatch-maintenance-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new ExternalProcessRunner();
            var video = Path.Combine(root, "video.mp4");
            var ass = Path.Combine(root, "subtitle.ass");
            var srt = Path.Combine(root, "subtitle.srt");
            var extraSrt = Path.Combine(root, "extra.srt");
            var tags = Path.Combine(root, "tags.xml");
            var source = Path.Combine(root, "source.mkv");
            await File.WriteAllTextAsync(ass, $$"""
                [Script Info]
                ScriptType: v4.00+
                PlayResX: 1920
                PlayResY: 1080
                [V4+ Styles]
                Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
                {{AppSettings.LegacyMalgunGothicAssStyleLine}}
                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,test
                """);
            await File.WriteAllTextAsync(
                srt,
                "1\r\n00:00:00,000 --> 00:00:01,000\r\n<font color = 112233 face = Arial>test</font>\r\n");
            await File.WriteAllTextAsync(
                extraSrt,
                "1\r\n00:00:00,000 --> 00:00:01,000\r\nremove me\r\n");
            await File.WriteAllTextAsync(tags, SubMuxMetadata.CreateGlobalTagsXml("2026.01.01"));
            var ffmpeg = await runner.RunAsync(new ProcessRequest(ffmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=320x180:d=2", "-c:v", "mpeg4", video], root));
            Assert.Equal(0, ffmpeg.ExitCode);
            var mux = await runner.RunAsync(new ProcessRequest(mkvMergePath,
                [
                    "-o", source, "--global-tags", tags, video,
                    "--language", "0:kor", "--track-name", "0:스타일 자막 (ASS)", ass,
                    "--language", "0:kor", "--track-name", "0:일반 자막 (SRT)", srt,
                    "--language", "0:eng", "--track-name", "0:Other subtitle", extraSrt
                ], root));
            Assert.True(mux.ExitCode is 0 or 1, mux.StandardError + Environment.NewLine + mux.StandardOutput);
            var extractedTags = await runner.RunAsync(new ProcessRequest(
                Path.Combine(Path.GetDirectoryName(mkvMergePath)!, "mkvextract.exe"), [source, "tags"], root));
            Assert.Contains(SubMuxMetadata.VersionTagName, extractedTags.StandardOutput);

            var settings = new AppSettings
            {
                MaintenanceUpdateAssStyle = true,
                MaintenanceUpdateFonts = false,
                MaintenanceApplyAudioSettings = false,
                MaintenanceRefreshTags = true,
                MaintenanceReplaceOriginal = true,
                RemoveExistingSubtitles = true,
                AssStyleLine = AppSettings.DefaultAssStyleLine
            };
            var dependency = new ToolDependency("MKVToolNix", "mkvmerge.exe", mkvMergePath, null);
            var result = await new MaintenanceProcessor(runner).ProcessAsync(
                source, settings, new DependencyReport(dependency));

            Assert.True(result.State is JobState.Succeeded or JobState.SucceededWithWarnings, result.Error);
            Assert.NotNull(result.OutputPath);
            Assert.Equal(source, result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            var inspection = await new MkvMergeClient(mkvMergePath, runner).InspectAsync(result.OutputPath!);
            Assert.Single(inspection.Tracks, track => string.Equals(track.Type, "video", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, inspection.Tracks.Count(track =>
                string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase)));
            Assert.DoesNotContain(inspection.Tracks, track =>
                string.Equals(track.TrackName, "Other subtitle", StringComparison.OrdinalIgnoreCase));
            var maintainedAssTrack = Assert.Single(inspection.Tracks, track => track.CodecId.Contains("ASS", StringComparison.OrdinalIgnoreCase));
            var maintainedAss = Path.Combine(root, "maintained-extracted.ass");
            var extract = await runner.RunAsync(new ProcessRequest(
                Path.Combine(Path.GetDirectoryName(mkvMergePath)!, "mkvextract.exe"),
                [result.OutputPath!, "tracks", $"{maintainedAssTrack.Id}:{maintainedAss}"], root));
            Assert.InRange(extract.ExitCode, 0, 1);
            var maintainedAssText = await File.ReadAllTextAsync(maintainedAss);
            Assert.Contains(AppSettings.DefaultAssStyleLine, maintainedAssText);
            Assert.Contains(@"\c&H332211&", maintainedAssText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"\fnArial", maintainedAssText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"; SUBMUX_SUBTITLE_SOURCE={SubMuxMetadata.LegacySrtOrSmiSource}", maintainedAssText);
            var maintainedTags = await runner.RunAsync(new ProcessRequest(
                Path.Combine(Path.GetDirectoryName(mkvMergePath)!, "mkvextract.exe"), [result.OutputPath!, "tags"], root));
            Assert.DoesNotContain(SubMuxMetadata.SubtitleSourceTagName, maintainedTags.StandardOutput);
            Assert.Contains(SubMuxMetadata.GetApplicationVersion(), maintainedTags.StandardOutput);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task MaintenanceLapseReplacesPairedSrtAndRegeneratedAssTogether()
    {
        var mkvMergePath = @"C:\Program Files\MKVToolNix\mkvmerge.exe";
        var ffmpegPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "dependencies", "ffmpeg", "win-x64", "ffmpeg.exe"));
        if (!File.Exists(mkvMergePath) || !File.Exists(ffmpegPath)) return;

        var root = Path.Combine(Path.GetTempPath(), $"SubMuxBatch-maintenance-lapse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new ExternalProcessRunner();
            var video = Path.Combine(root, "video.mp4");
            var ass = Path.Combine(root, "subtitle.ass");
            var srt = Path.Combine(root, "subtitle.srt");
            var tags = Path.Combine(root, "tags.xml");
            var source = Path.Combine(root, "source.mkv");
            var assText = SubMuxMetadata.AddOrReplaceSubtitleSourceMarker(
                $$"""
                [Script Info]
                ScriptType: v4.00+
                PlayResX: 1920
                PlayResY: 1080
                [V4+ Styles]
                Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
                {{AppSettings.LegacyMalgunGothicAssStyleLine}}
                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,test
                """,
                "SRT");
            await File.WriteAllTextAsync(ass, assText);
            await File.WriteAllTextAsync(srt, "1\r\n00:00:00,000 --> 00:00:01,000\r\ntest\r\n");
            await File.WriteAllTextAsync(tags, SubMuxMetadata.CreateGlobalTagsXml("2026.01.01"));
            var ffmpeg = await runner.RunAsync(new ProcessRequest(ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "color=c=black:s=320x180:d=3",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                    "-shortest", "-c:v", "mpeg4", "-c:a", "aac", video
                ], root));
            Assert.Equal(0, ffmpeg.ExitCode);
            var mux = await runner.RunAsync(new ProcessRequest(mkvMergePath,
                [
                    "-o", source, "--global-tags", tags, video,
                    "--language", "0:kor", "--track-name", "0:스타일 자막 (ASS)", ass,
                    "--language", "0:kor", "--track-name", "0:일반 자막 (SRT)", "--default-track-flag", "0:yes", srt
                ], root));
            Assert.True(mux.ExitCode is 0 or 1, $"{mux.StandardError}\n{mux.StandardOutput}");

            var settings = new AppSettings
            {
                EnableLapseSync = true,
                MaintenanceUpdateLapseSync = true,
                MaintenanceUpdateAssStyle = true,
                MaintenanceUpdateFonts = false,
                MaintenanceApplyAudioSettings = false,
                MaintenanceRefreshTags = true,
                MaintenanceReplaceOriginal = true,
                AssStyleLine = AppSettings.DefaultAssStyleLine
            };
            var dependency = new ToolDependency("MKVToolNix", "mkvmerge.exe", mkvMergePath, null);
            var result = await new MaintenanceProcessor(
                    runner,
                    lapseSynchronizer: new OneSecondShiftLapseSynchronizer())
                .ProcessAsync(source, settings, new DependencyReport(dependency));

            Assert.True(result.State is JobState.Succeeded or JobState.SucceededWithWarnings, result.Error);
            var inspection = await new MkvMergeClient(mkvMergePath, runner).InspectAsync(source);
            var assTrack = Assert.Single(inspection.Tracks, track => track.CodecId.Contains("ASS", StringComparison.OrdinalIgnoreCase));
            var srtTrack = Assert.Single(inspection.Tracks, track => track.CodecId.Contains("UTF8", StringComparison.OrdinalIgnoreCase));
            var extractedAss = Path.Combine(root, "result.ass");
            var extractedSrt = Path.Combine(root, "result.srt");
            var mkvExtract = Path.Combine(Path.GetDirectoryName(mkvMergePath)!, "mkvextract.exe");
            Assert.InRange((await runner.RunAsync(new ProcessRequest(
                mkvExtract, [source, "tracks", $"{assTrack.Id}:{extractedAss}", $"{srtTrack.Id}:{extractedSrt}"], root))).ExitCode, 0, 1);
            var outputAss = await File.ReadAllTextAsync(extractedAss);
            var outputSrt = await File.ReadAllTextAsync(extractedSrt);
            Assert.Contains("0:00:01.00,0:00:02.00", outputAss);
            Assert.Contains("00:00:01,000 --> 00:00:02,000", outputSrt);
            Assert.Contains("; SUBMUX_LAPSE_PROFILE=AUTO|AUTO|6|8", outputAss);
            Assert.Contains("; SUBMUX_SUBTITLE_SOURCE=SRT", outputAss);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task MetadataAndChapterCleanupWorkWithoutTagRefreshAndKeepOnlyFreshSubMuxTags()
    {
        var mkvMergePath = @"C:\Program Files\MKVToolNix\mkvmerge.exe";
        var ffmpegPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "dependencies", "ffmpeg", "win-x64", "ffmpeg.exe"));
        if (!File.Exists(mkvMergePath) || !File.Exists(ffmpegPath)) return;

        var root = Path.Combine(Path.GetTempPath(), $"SubMuxBatch-maintenance-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new ExternalProcessRunner();
            var video = Path.Combine(root, "video.mp4");
            var ass = Path.Combine(root, "subtitle.ass");
            var srt = Path.Combine(root, "subtitle.srt");
            var tags = Path.Combine(root, "tags.xml");
            var chapters = Path.Combine(root, "chapters.xml");
            var source = Path.Combine(root, "source.mkv");
            await File.WriteAllTextAsync(ass, SubMuxMetadata.AddOrReplaceSubtitleSourceMarker(
                "[Script Info]\nScriptType: v4.00+\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,test\n",
                "ASS"));
            await File.WriteAllTextAsync(srt, "1\r\n00:00:00,000 --> 00:00:01,000\r\ntest\r\n");
            await File.WriteAllTextAsync(tags, SubMuxMetadata.CreateGlobalTagsXml("2026.01.01").Replace(
                "</Tag>",
                "<Simple><Name>ORIGINAL_TAG</Name><String>remove me</String></Simple></Tag>",
                StringComparison.Ordinal));
            await File.WriteAllTextAsync(chapters, """
                <?xml version="1.0" encoding="UTF-8"?>
                <Chapters><EditionEntry><ChapterAtom><ChapterTimeStart>00:00:00.000</ChapterTimeStart><ChapterDisplay><ChapterString>Start</ChapterString><ChapterLanguage>eng</ChapterLanguage></ChapterDisplay></ChapterAtom></EditionEntry></Chapters>
                """);
            var ffmpeg = await runner.RunAsync(new ProcessRequest(ffmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=320x180:d=2", "-c:v", "mpeg4", video], root));
            Assert.Equal(0, ffmpeg.ExitCode);
            var mux = await runner.RunAsync(new ProcessRequest(mkvMergePath,
                [
                    "-o", source,
                    "--title", "Release title",
                    "--global-tags", tags,
                    "--chapters", chapters,
                    "--track-name", "0:Release video", video,
                    "--language", "0:kor", "--track-name", "0:스타일 자막 (ASS)", ass,
                    "--language", "0:kor", "--track-name", "0:일반 자막 (SRT)", srt
                ], root));
            Assert.True(mux.ExitCode is 0 or 1, $"{mux.StandardError}\n{mux.StandardOutput}");

            var settings = new AppSettings
            {
                MaintenanceUpdateAssStyle = false,
                MaintenanceUpdateFonts = false,
                MaintenanceApplyAudioSettings = false,
                MaintenanceRefreshTags = false,
                MaintenanceUpdateLapseSync = false,
                MaintenanceReplaceOriginal = true,
                CleanOutputMetadata = true,
                RemoveChapters = true,
                AddSubMuxTag = true
            };
            var dependency = new ToolDependency("MKVToolNix", "mkvmerge.exe", mkvMergePath, null);
            var result = await new MaintenanceProcessor(runner).ProcessAsync(
                source, settings, new DependencyReport(dependency));

            Assert.True(result.State is JobState.Succeeded or JobState.SucceededWithWarnings, result.Error);
            var inspection = await new MkvMergeClient(mkvMergePath, runner).InspectAsync(source);
            Assert.Equal(0, inspection.ChapterCount);
            Assert.All(inspection.Tracks.Where(track => string.Equals(track.Type, "video", StringComparison.OrdinalIgnoreCase)),
                track => Assert.True(string.IsNullOrWhiteSpace(track.TrackName)));
            var extractedTags = await runner.RunAsync(new ProcessRequest(
                Path.Combine(Path.GetDirectoryName(mkvMergePath)!, "mkvextract.exe"), [source, "tags"], root));
            Assert.DoesNotContain("ORIGINAL_TAG", extractedTags.StandardOutput);
            Assert.Contains(SubMuxMetadata.VersionTagName, extractedTags.StandardOutput);
            Assert.Contains(SubMuxMetadata.ProcessedTagName, extractedTags.StandardOutput);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class OneSecondShiftLapseSynchronizer : ILapseSynchronizer
    {
        public async Task<LapseSyncResult> SynchronizeAsync(
            LapseSyncRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            var text = await File.ReadAllTextAsync(request.SubtitlePath, cancellationToken);
            text = text.Replace(
                "00:00:00,000 --> 00:00:01,000",
                "00:00:01,000 --> 00:00:02,000",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(request.OutputPath, text, cancellationToken);
            return new LapseSyncResult(
                LapseVerdict.Solid,
                "auto/shifted",
                "embedded",
                1000,
                1,
                1,
                1,
                [],
                request.OutputPath,
                null);
        }
    }
}
