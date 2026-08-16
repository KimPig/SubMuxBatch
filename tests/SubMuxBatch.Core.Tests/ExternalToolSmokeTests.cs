using System.Text;
using System.Xml.Linq;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Fonts;
using SubMuxBatch.Core.Media;
using SubMuxBatch.Core.Planning;
using SubMuxBatch.Core.Processing;
using Xunit.Abstractions;

namespace SubMuxBatch.Core.Tests;

public sealed class ExternalToolSmokeTests(ITestOutputHelper output)
{
    private const string ArialStyleLine =
        "Style: Default,Arial,42,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,2,1,2,20,20,30,1";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealMkvMergeIncludesAddedFontAttachment()
    {
        var mkvMergePath = FindExecutable(
            "MKVMERGE_PATH",
            "mkvmerge.exe",
            @"C:\Program Files\MKVToolNix\mkvmerge.exe");
        if (mkvMergePath is null)
        {
            output.WriteLine("mkvmerge를 찾지 못해 실제 폰트 첨부 smoke test를 건너뜁니다.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-font-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourceSubtitle = Path.Combine(root, "source.srt");
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var font = Path.Combine(root, "test-family.ttf");
            var globalTags = Path.Combine(root, "submux-tags.xml");
            var outputPath = Path.Combine(root, "output.mkv");
            await File.WriteAllTextAsync(sourceSubtitle, "1\n00:00:00,000 --> 00:00:01,000\nSource\n");
            await File.WriteAllTextAsync(
                ass,
                "[Script Info]\nScriptType: v4.00+\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Test Family,40,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,2,1,2,20,20,30,1\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Test\n");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nTest\n");
            await File.WriteAllBytesAsync(font, [1, 2, 3, 4, 5]);
            var tagDocument = XDocument.Parse(SubMuxMetadata.CreateGlobalTagsXml("2026.08.17"));
            tagDocument.Root?.Element("Tag")?.Add(
                new XElement("Simple",
                    new XElement("Name", "TITLE"),
                    new XElement("String", "Metadata smoke title")));
            await File.WriteAllTextAsync(globalTags, tagDocument.ToString());

            var runner = new ExternalProcessRunner();
            var sourceResult = await runner.RunAsync(new ProcessRequest(
                mkvMergePath,
                ["-o", source, sourceSubtitle],
                root));
            Assert.InRange(sourceResult.ExitCode, 0, 1);

            var attachment = new FontAttachmentFile(font, "font/ttf");
            var client = new MkvMergeClient(mkvMergePath, runner);
            await client.MuxAsync(
                source,
                ass,
                srt,
                outputPath,
                fontAttachments: [attachment],
                globalTagsPath: globalTags);

            var sourceInspection = await client.InspectAsync(source);
            var outputInspection = await client.InspectAsync(outputPath);
            Assert.Empty(MkvMergeClient.ValidateOutput(
                sourceInspection,
                outputInspection,
                addedFontAttachments: [attachment]));
            var added = Assert.Single(outputInspection.Attachments);
            Assert.Equal("test-family.ttf", added.FileName);
            Assert.Equal("font/ttf", added.ContentType);
            var mediaInfo = new MediaInfoClient().Inspect(outputPath);
            Assert.True(mediaInfo.ProcessedBySubMux);
            Assert.Equal("2026.08.17", mediaInfo.SubMuxBatchVersion);
            Assert.Equal(SubMuxMetadata.CommentValue, mediaInfo.Comment);
            var metadataTag = Assert.Single(mediaInfo.MetadataTags);
            Assert.Equal("Title", metadataTag.Name, ignoreCase: true);
            Assert.Equal("Metadata smoke title", metadataTag.Value);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealToolsConvertAndMuxExpectedTracks()
    {
        var mkvMergePath = FindExecutable(
            "MKVMERGE_PATH",
            "mkvmerge.exe",
            @"C:\Program Files\MKVToolNix\mkvmerge.exe");
        var seConvPath = FindExecutable(
            "SECONV_PATH",
            "seconv.exe",
            @"C:\Program Files\Subtitle Edit\seconv.exe");
        var ffmpegPath = FindExecutable(
            "FFMPEG_PATH",
            "ffmpeg.exe",
            @"C:\Program Files\Jellyfin\Server\ffmpeg.exe");

        if (mkvMergePath is null || seConvPath is null || ffmpegPath is null)
        {
            output.WriteLine("외부 도구가 모두 설정되지 않아 실제 도구 smoke test를 건너뜁니다.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"[Release Group] submux-batch-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var runner = new ExternalProcessRunner();
            var rawPath = Path.Combine(root, "raw.mkv");
            var sourcePath = Path.Combine(root, "[Group] Movie.mkv");
            var srtPath = Path.Combine(root, "[Group] Movie.srt");
            var oldInternalSrtPath = Path.Combine(root, "old-internal.srt");
            var attachmentPath = Path.Combine(root, "dummy-font.ttf");
            var nonFontAttachmentPath = Path.Combine(root, "metadata.txt");
            var chaptersPath = Path.Combine(root, "chapters.txt");

            var ffmpeg = await runner.RunAsync(new ProcessRequest(
                ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "color=c=black:s=320x180:r=24",
                    "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                    "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                    "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0",
                    "-t", "0.5", "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", rawPath
                ],
                root));
            Assert.Equal(0, ffmpeg.ExitCode);
            Assert.True(File.Exists(rawPath));

            await File.WriteAllTextAsync(attachmentPath, "smoke attachment", Encoding.ASCII);
            await File.WriteAllTextAsync(nonFontAttachmentPath, "keep this attachment", Encoding.ASCII);
            await File.WriteAllTextAsync(
                chaptersPath,
                "CHAPTER01=00:00:00.000\r\nCHAPTER01NAME=시작\r\n",
                new UTF8Encoding(true));
            await File.WriteAllTextAsync(
                oldInternalSrtPath,
                "1\r\n00:00:00,000 --> 00:00:00,400\r\nold internal subtitle\r\n",
                new UTF8Encoding(false));
            var attach = await runner.RunAsync(new ProcessRequest(
                mkvMergePath,
                [
                    "-o", sourcePath,
                    "--chapters", chaptersPath,
                    "--attachment-name", "dummy-font.ttf",
                    "--attachment-mime-type", "application/x-truetype-font",
                    "--attach-file", attachmentPath,
                    "--attachment-name", "metadata.txt",
                    "--attachment-mime-type", "text/plain",
                    "--attach-file", nonFontAttachmentPath,
                    "--language", "1:eng",
                    "--track-name", "1:English",
                    "--default-track-flag", "1:yes",
                    "--language", "2:jpn",
                    "--track-name", "2:Japanese",
                    "--default-track-flag", "2:no",
                    rawPath,
                    "--language", "0:eng",
                    "--default-track-flag", "0:yes",
                    oldInternalSrtPath
                ],
                root));
            Assert.InRange(attach.ExitCode, 0, 1);

            await File.WriteAllTextAsync(
                srtPath,
                "1\r\n00:00:00,000 --> 00:00:00,200\r\n<font color=\"#FF0000\">테스트</font>\r\n\r\n"
                + "2\r\n00:00:00,200 --> 00:00:00,400\r\n{\\an8}위쪽\r\n",
                new UTF8Encoding(false));

            var settings = new AppSettings
            {
                OutputPrefix = "result_",
                AssStyleLine = ArialStyleLine,
                PlayResX = 1280,
                PlayResY = 720,
                AttachAssStyleFonts = false
            };

            var stylePath = Path.Combine(root, "style.ass");
            var convertedAssPath = Path.Combine(root, "converted.ass");
            await File.WriteAllTextAsync(stylePath, AssStyleTemplateWriter.Create(settings), new UTF8Encoding(true));
            await new SeConvClient(seConvPath, runner).ConvertAsync(
                srtPath,
                convertedAssPath,
                SubtitleOutputFormat.AdvancedSubStationAlpha,
                stylePath,
                settings.PlayResX,
                settings.PlayResY);
            var convertedAss = await File.ReadAllTextAsync(convertedAssPath);
            Assert.Contains("Style: Default,Arial,42", convertedAss);
            Assert.Contains(@"{\c&H0000ff&}", convertedAss, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"{\an8}", convertedAss, StringComparison.OrdinalIgnoreCase);

            var media = new MediaSet(
                new MediaKey(root, "[Group] Movie"),
                sourcePath,
                null,
                srtPath,
                null);
            var plan = ConversionPlanFactory.Create(media);
            var dependencies = new DependencyReport(
                new ToolDependency("MKVToolNix", "mkvmerge.exe", mkvMergePath, "smoke"),
                new ToolDependency("Subtitle Edit seconv", "seconv.exe", seConvPath, "smoke"));

            var result = await new BatchProcessor(runner).ProcessAsync(media, plan, settings, dependencies);

            Assert.True(result.State is JobState.Succeeded or JobState.SucceededWithWarnings, result.Error);
            Assert.NotNull(result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.True(File.Exists(sourcePath));
            Assert.True(File.Exists(srtPath));

            var mkvMerge = new MkvMergeClient(mkvMergePath, runner);
            var sourceInspection = await mkvMerge.InspectAsync(sourcePath);
            var outputInspection = await mkvMerge.InspectAsync(result.OutputPath!);
            Assert.Empty(MkvMergeClient.ValidateOutput(sourceInspection, outputInspection));
            Assert.Equal(2, outputInspection.AttachmentCount);
            Assert.Equal(1, sourceInspection.ChapterCount);
            Assert.Equal(1, outputInspection.ChapterCount);
            Assert.Single(sourceInspection.Tracks, static track => track.Type == "subtitles");
            Assert.Equal(2, outputInspection.Tracks.Count(static track => track.Type == "subtitles"));

            settings.OutputPrefix = "preserved_";
            settings.RemoveExistingSubtitles = false;
            var preservedResult = await new BatchProcessor(runner).ProcessAsync(media, plan, settings, dependencies);
            Assert.True(
                preservedResult.State is JobState.Succeeded or JobState.SucceededWithWarnings,
                preservedResult.Error);
            var preservedInspection = await mkvMerge.InspectAsync(preservedResult.OutputPath!);
            Assert.Empty(MkvMergeClient.ValidateOutput(
                sourceInspection,
                preservedInspection,
                removeExistingSubtitles: false));
            var preservedSubtitles = preservedInspection.Tracks
                .Where(static track => track.Type == "subtitles")
                .ToArray();
            Assert.Equal(3, preservedSubtitles.Length);
            Assert.False(preservedSubtitles[0].DefaultTrack);
            Assert.True(preservedSubtitles[1].DefaultTrack);
            Assert.False(preservedSubtitles[2].DefaultTrack);

            settings.OutputPrefix = "fontless_";
            settings.RemoveExistingSubtitles = true;
            settings.RemoveExistingFontAttachments = true;
            var fontlessResult = await new BatchProcessor(runner).ProcessAsync(media, plan, settings, dependencies);
            Assert.True(
                fontlessResult.State is JobState.Succeeded or JobState.SucceededWithWarnings,
                fontlessResult.Error);
            var fontlessInspection = await mkvMerge.InspectAsync(fontlessResult.OutputPath!);
            Assert.Empty(MkvMergeClient.ValidateOutput(
                sourceInspection,
                fontlessInspection,
                removeExistingFontAttachments: true));
            var retainedAttachment = Assert.Single(fontlessInspection.Attachments);
            Assert.Equal("metadata.txt", retainedAttachment.FileName);
            Assert.Equal("text/plain", retainedAttachment.ContentType);

            settings.OutputPrefix = "japanese_";
            settings.RemoveExistingFontAttachments = false;
            settings.FilterAudioTracksByLanguage = true;
            settings.SelectedAudioLanguage = AudioTrackLanguage.Japanese;
            var japaneseResult = await new BatchProcessor(runner).ProcessAsync(media, plan, settings, dependencies);
            Assert.True(
                japaneseResult.State is JobState.Succeeded or JobState.SucceededWithWarnings,
                japaneseResult.Error);
            var japaneseInspection = await mkvMerge.InspectAsync(japaneseResult.OutputPath!);
            Assert.Empty(MkvMergeClient.ValidateOutput(
                sourceInspection,
                japaneseInspection,
                keepOnlyAudioLanguage: AudioTrackLanguage.Japanese));
            var japaneseAudio = Assert.Single(
                japaneseInspection.Tracks, static track => track.Type == "audio");
            Assert.True(japaneseAudio.DefaultTrack);
            Assert.True(
                string.Equals(japaneseAudio.Language, "jpn", StringComparison.OrdinalIgnoreCase)
                || string.Equals(japaneseAudio.LanguageIetf, "ja", StringComparison.OrdinalIgnoreCase)
                || japaneseAudio.LanguageIetf?.StartsWith("ja-", StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealToolsRemuxMp4ToMkvWithoutReencoding()
    {
        var mkvMergePath = FindExecutable(
            "MKVMERGE_PATH",
            "mkvmerge.exe",
            @"C:\Program Files\MKVToolNix\mkvmerge.exe");
        var seConvPath = FindExecutable(
            "SECONV_PATH",
            "seconv.exe",
            @"C:\Program Files\Subtitle Edit\seconv.exe");
        var ffmpegPath = FindExecutable(
            "FFMPEG_PATH",
            "ffmpeg.exe",
            @"C:\Program Files\Jellyfin\Server\ffmpeg.exe");
        if (mkvMergePath is null || seConvPath is null || ffmpegPath is null)
        {
            output.WriteLine("외부 도구가 모두 설정되지 않아 MP4 smoke test를 건너뜁니다.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-mp4-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new ExternalProcessRunner();
            var sourcePath = Path.Combine(root, "Movie.mp4");
            var srtPath = Path.Combine(root, "Movie.srt");
            var internalSrtPath = Path.Combine(root, "internal.srt");
            await File.WriteAllTextAsync(
                internalSrtPath,
                "1\r\n00:00:00,000 --> 00:00:00,400\r\nInternal subtitle\r\n",
                new UTF8Encoding(false));
            var ffmpeg = await runner.RunAsync(new ProcessRequest(
                ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "color=c=black:s=320x180:r=24",
                    "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                    "-i", internalSrtPath,
                    "-map", "0:v:0", "-map", "1:a:0", "-map", "2:s:0", "-t", "0.5",
                    "-c:v", "libx264", "-c:a", "aac", "-c:s", "mov_text",
                    "-metadata:s:s:0", "language=eng", "-shortest", sourcePath
                ],
                root));
            Assert.Equal(0, ffmpeg.ExitCode);
            await File.WriteAllTextAsync(
                srtPath,
                "1\r\n00:00:00,000 --> 00:00:00,400\r\nMP4 smoke\r\n",
                new UTF8Encoding(false));

            var media = new MediaSet(new MediaKey(root, "Movie"), sourcePath, null, srtPath, null);
            var result = await new BatchProcessor(runner).ProcessAsync(
                media,
                ConversionPlanFactory.Create(media),
                new AppSettings { OutputPrefix = "result_", AssStyleLine = ArialStyleLine, AttachAssStyleFonts = false },
                new DependencyReport(
                    new ToolDependency("MKVToolNix", "mkvmerge.exe", mkvMergePath, "smoke"),
                    new ToolDependency("Subtitle Edit seconv", "seconv.exe", seConvPath, "smoke")));

            Assert.True(result.State is JobState.Succeeded or JobState.SucceededWithWarnings, result.Error);
            Assert.Equal(Path.Combine(root, "result_Movie.mkv"), result.OutputPath);
            Assert.True(File.Exists(sourcePath));
            var mkvMerge = new MkvMergeClient(mkvMergePath, runner);
            var sourceInspection = await mkvMerge.InspectAsync(sourcePath);
            Assert.Empty(MkvMergeClient.ValidateOutput(
                sourceInspection,
                await mkvMerge.InspectAsync(result.OutputPath!)));

            var preserveSettings = new AppSettings
            {
                OutputPrefix = "preserved_",
                AssStyleLine = ArialStyleLine,
                RemoveExistingSubtitles = false,
                AttachAssStyleFonts = false
            };
            var preservedResult = await new BatchProcessor(runner).ProcessAsync(
                media,
                ConversionPlanFactory.Create(media),
                preserveSettings,
                new DependencyReport(
                    new ToolDependency("MKVToolNix", "mkvmerge.exe", mkvMergePath, "smoke"),
                    new ToolDependency("Subtitle Edit seconv", "seconv.exe", seConvPath, "smoke")));
            Assert.True(
                preservedResult.State is JobState.Succeeded or JobState.SucceededWithWarnings,
                preservedResult.Error);
            Assert.Empty(MkvMergeClient.ValidateOutput(
                sourceInspection,
                await mkvMerge.InspectAsync(preservedResult.OutputPath!),
                removeExistingSubtitles: false));
        }
        finally
        {
            TryDelete(root);
        }
    }
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealSeConvUsesCp949FallbackForSmi()
    {
        var seConvPath = FindExecutable(
            "SECONV_PATH",
            "seconv.exe",
            @"C:\Program Files\Subtitle Edit\seconv.exe");
        if (seConvPath is null)
        {
            output.WriteLine("seconv가 설정되지 않아 CP949 smoke test를 건너뜁니다.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"[Group] submux-batch-smi-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var smiPath = Path.Combine(root, "Korean.smi");
            var srtPath = Path.Combine(root, "Korean.srt");
            var normalizedSrtPath = Path.Combine(root, "normalized.srt");
            var stylePath = Path.Combine(root, "style.ass");
            var assPath = Path.Combine(root, "Korean.ass");
            var roundTripSrtPath = Path.Combine(root, "round-trip.srt");
            const string smi = "<SAMI><BODY><SYNC Start=0><P Class=KRCC><FONT COLOR=\"#00FF00\"><B>안녕하세요</B></FONT> <RUBY><RB>漢</RB><RT>かん</RT></RUBY><SYNC Start=1000><P Class=KRCC>&nbsp;</BODY></SAMI>";
            await File.WriteAllTextAsync(smiPath, smi, Encoding.GetEncoding(949));

            var client = new SeConvClient(seConvPath, new ExternalProcessRunner());
            await client.ConvertAsync(
                smiPath,
                srtPath,
                SubtitleOutputFormat.SubRip,
                null,
                1280,
                720);

            var converted = await File.ReadAllTextAsync(srtPath);
            Assert.Contains("안녕하세요", converted);
            Assert.DoesNotContain("&nbsp;", converted, StringComparison.OrdinalIgnoreCase);

            await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(srtPath, normalizedSrtPath);
            var normalized = await File.ReadAllTextAsync(normalizedSrtPath);
            Assert.DoesNotContain("<FONT", normalized, StringComparison.Ordinal);
            Assert.DoesNotContain("<RUBY", normalized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("漢(かん)", normalized);

            await File.WriteAllTextAsync(
                stylePath,
                AssStyleTemplateWriter.Create(new AppSettings { AssStyleLine = ArialStyleLine }),
                new UTF8Encoding(true));
            await client.ConvertAsync(
                normalizedSrtPath,
                assPath,
                SubtitleOutputFormat.AdvancedSubStationAlpha,
                stylePath,
                1280,
                720);
            var ass = await File.ReadAllTextAsync(assPath);
            Assert.DoesNotContain("<FONT", ass, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<RUBY", ass, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"\c&H00ff00&", ass, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"\b1", ass, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("漢(かん)", ass);

            await client.ConvertAsync(
                assPath,
                roundTripSrtPath,
                SubtitleOutputFormat.SubRip,
                null,
                1280,
                720);
            Assert.Contains("안녕하세요", await File.ReadAllTextAsync(roundTripSrtPath));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string? FindExecutable(string environmentVariable, string executableName, params string[] candidates)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        return pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path.Trim('"'), executableName))
            .FirstOrDefault(File.Exists);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A failed test should not be hidden by temporary cleanup failure.
        }
    }
}
