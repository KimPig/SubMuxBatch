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
            await File.WriteAllTextAsync(tags, SubMuxMetadata.CreateGlobalTagsXml("2026.01.01"));
            var ffmpeg = await runner.RunAsync(new ProcessRequest(ffmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=320x180:d=2", "-c:v", "mpeg4", video], root));
            Assert.Equal(0, ffmpeg.ExitCode);
            var mux = await runner.RunAsync(new ProcessRequest(mkvMergePath,
                ["-o", source, "--global-tags", tags, video, "--language", "0:kor", "--track-name", "0:스타일 자막 (ASS)", ass], root));
            Assert.InRange(mux.ExitCode, 0, 1);
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
            var maintainedAssTrack = Assert.Single(inspection.Tracks, track => track.CodecId.Contains("ASS", StringComparison.OrdinalIgnoreCase));
            var maintainedAss = Path.Combine(root, "maintained-extracted.ass");
            var extract = await runner.RunAsync(new ProcessRequest(
                Path.Combine(Path.GetDirectoryName(mkvMergePath)!, "mkvextract.exe"),
                [result.OutputPath!, "tracks", $"{maintainedAssTrack.Id}:{maintainedAss}"], root));
            Assert.InRange(extract.ExitCode, 0, 1);
            var maintainedAssText = await File.ReadAllTextAsync(maintainedAss);
            Assert.Contains(AppSettings.DefaultAssStyleLine, maintainedAssText);
            Assert.Contains($"; SUBMUX_ASS_SOURCE={SubMuxMetadata.LegacySrtOrSmiSource}", maintainedAssText);
            var maintainedTags = await runner.RunAsync(new ProcessRequest(
                Path.Combine(Path.GetDirectoryName(mkvMergePath)!, "mkvextract.exe"), [result.OutputPath!, "tags"], root));
            Assert.DoesNotContain(SubMuxMetadata.AssSourceTagName, maintainedTags.StandardOutput);
            Assert.Contains(SubMuxMetadata.GetApplicationVersion(), maintainedTags.StandardOutput);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
