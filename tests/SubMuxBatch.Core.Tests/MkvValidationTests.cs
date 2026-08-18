using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Fonts;

namespace SubMuxBatch.Core.Tests;

public sealed class MkvValidationTests
{
    [Fact]
    public void AcceptsAssDefaultThenSrtSecondaryAndPreservedMedia()
    {
        var source = new MkvInspection(
            [Track("video", "V_MPEGH/ISO/HEVC"), Track("audio", "A_OPUS")],
            [Attachment("font-a.ttf", "1"), Attachment("font-b.otf", "2")],
            4);
        var output = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                Track("audio", "A_OPUS"),
                Track("subtitles", "S_TEXT/ASS", true, false, "kor"),
                Track("subtitles", "S_TEXT/UTF8", false, false, "kor")
            ],
            [Attachment("font-a.ttf", "1"), Attachment("font-b.otf", "2")],
            4);

        Assert.Empty(MkvMergeClient.ValidateOutput(source, output));
    }

    [Fact]
    public void AcceptsContainerSpecificCodecIdsAndAddedIetfLanguageMetadata()
    {
        var source = new MkvInspection(
            [
                new MkvTrackInfo("video", "AVC/H.264/MPEG-4p10", true, false, null, null, null, CodecName: "AVC/H.264/MPEG-4p10"),
                new MkvTrackInfo("audio", "AAC", true, false, "jpn", null, "Japanese", CodecName: "AAC")
            ],
            [],
            0);
        var output = new MkvInspection(
            [
                new MkvTrackInfo("video", "V_MPEG4/ISO/AVC", true, false, null, null, null, CodecName: "AVC/H.264/MPEG-4p10"),
                new MkvTrackInfo("audio", "A_AAC", true, false, "jpn", "ja", "Japanese", CodecName: "AAC"),
                Track("subtitles", "S_TEXT/ASS", true, false, "kor"),
                Track("subtitles", "S_TEXT/UTF8", false, false, "kor")
            ],
            [],
            0);

        Assert.Empty(MkvMergeClient.ValidateOutput(
            source,
            output,
            keepOnlyAudioLanguage: AudioTrackLanguage.Japanese));
    }
    [Fact]
    public void AcceptsMp4TimedTextConvertedToMatroskaUtf8Subtitle()
    {
        var source = new MkvInspection(
            [
                Track("video", "AVC/H.264/MPEG-4p10"),
                new MkvTrackInfo("subtitles", "Timed Text", true, false, "eng", null, null, CodecName: "Timed Text")
            ],
            [],
            0);
        var output = new MkvInspection(
            [
                Track("video", "AVC/H.264/MPEG-4p10"),
                new MkvTrackInfo("subtitles", "S_TEXT/UTF8", false, false, "eng", "en", null, CodecName: "SubRip/SRT"),
                Track("subtitles", "S_TEXT/ASS", true, false, "kor"),
                Track("subtitles", "S_TEXT/UTF8", false, false, "kor")
            ],
            [],
            0);

        Assert.Empty(MkvMergeClient.ValidateOutput(
            source,
            output,
            removeExistingSubtitles: false));
    }
    [Fact]
    public void RejectsWrongSubtitleOrderAndAttachmentLoss()
    {
        var source = new MkvInspection(
            [Track("video", "V_MPEGH/ISO/HEVC")],
            [Attachment("font.ttf", "1")],
            null);
        var output = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                Track("subtitles", "S_TEXT/UTF8", false, false, "kor"),
                Track("subtitles", "S_TEXT/ASS", true, false, "kor")
            ],
            [],
            null);

        var errors = MkvMergeClient.ValidateOutput(source, output);
        Assert.Contains(errors, error => error.Contains("첨부"));
        Assert.Contains(errors, error => error.Contains("S_TEXT/ASS"));
    }

    [Fact]
    public void RejectsChangedAttachmentEvenWhenCountMatches()
    {
        var source = new MkvInspection(
            [Track("video", "V_MPEGH/ISO/HEVC")],
            [Attachment("font-a.ttf", "1")],
            1);
        var output = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                Track("subtitles", "S_TEXT/ASS", true, false, "kor"),
                Track("subtitles", "S_TEXT/UTF8", false, false, "kor")
            ],
            [Attachment("font-b.ttf", "2")],
            1);

        var errors = MkvMergeClient.ValidateOutput(source, output);

        Assert.Contains(errors, error => error.Contains("첨부 파일 정보"));
    }

    [Fact]
    public void PreservesExistingSubtitleTracksAndAppendsNewDefaults()
    {
        var source = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                Track("subtitles", "S_HDMV/PGS", true, true, "jpn", "Japanese signs", 2),
                Track("subtitles", "S_TEXT/UTF8", false, false, "eng", "English", 4)
            ],
            [Attachment("font.ttf", "1")],
            2);
        var output = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                Track("subtitles", "S_HDMV/PGS", false, true, "jpn", "Japanese signs", 1),
                Track("subtitles", "S_TEXT/UTF8", false, false, "eng", "English", 2),
                Track("subtitles", "S_TEXT/ASS", true, false, "kor", "스타일 자막 (ASS)", 3),
                Track("subtitles", "S_TEXT/UTF8", false, false, "kor", "일반 자막 (SRT)", 4)
            ],
            [Attachment("font.ttf", "1")],
            2);

        Assert.Empty(MkvMergeClient.ValidateOutput(source, output, removeExistingSubtitles: false));
    }

    [Fact]
    public void RejectsChangedOrStillDefaultExistingSubtitle()
    {
        var source = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                Track("subtitles", "S_HDMV/PGS", true, false, "jpn", "Japanese", 2)
            ],
            [],
            0);
        var output = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                Track("subtitles", "S_TEXT/UTF8", true, false, "eng", "Changed", 1),
                Track("subtitles", "S_TEXT/ASS", true, false, "kor", "스타일 자막 (ASS)", 2),
                Track("subtitles", "S_TEXT/UTF8", false, false, "kor", "일반 자막 (SRT)", 3)
            ],
            [],
            0);

        var errors = MkvMergeClient.ValidateOutput(source, output, removeExistingSubtitles: false);

        Assert.Contains(errors, error => error.Contains("코덱이 보존"));
        Assert.Contains(errors, error => error.Contains("언어 정보"));
        Assert.Contains(errors, error => error.Contains("트랙 이름"));
        Assert.Contains(errors, error => error.Contains("기본 플래그가 해제"));
        Assert.Contains(errors, error => error.Contains("유일한 기본"));
    }

    [Fact]
    public void ChapterRemovalRequiresAnOutputWithoutChapters()
    {
        var source = new MkvInspection(
            [Track("video", "V_MPEGH/ISO/HEVC")],
            [],
            4);
        var outputWithoutChapters = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                Track("subtitles", "S_TEXT/ASS", true, false, "kor"),
                Track("subtitles", "S_TEXT/UTF8", false, false, "kor")
            ],
            [],
            0);
        var outputWithChapters = outputWithoutChapters with { ChapterCount = 1 };

        Assert.Empty(MkvMergeClient.ValidateOutput(
            source,
            outputWithoutChapters,
            removeChapters: true));
        Assert.NotEmpty(MkvMergeClient.ValidateOutput(
            source,
            outputWithChapters,
            removeChapters: true));
    }

    [Fact]
    public async Task MuxChapterRemovalUsesInputOptionBeforeSourceFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-chapter-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[V4+ Styles]\nStyle: Default,Arial,40\n[Events]");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");

            var runner = new MuxArgumentRunner(output);
            await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output,
                removeChapters: true);

            Assert.NotNull(runner.MuxArguments);
            var chapterOptionIndex = runner.MuxArguments!.IndexOf("--no-chapters");
            Assert.True(chapterOptionIndex >= 0);
            Assert.True(chapterOptionIndex < runner.MuxArguments.IndexOf(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FontRemovalPreservesOnlyNonFontAttachments()
    {
        var source = new MkvInspection(
            [Track("video", "V_MPEGH/ISO/HEVC")],
            [
                Attachment("font-a.ttf", "1", "application/x-truetype-font", 3),
                Attachment("cover.jpg", "2", "image/jpeg", 7),
                Attachment("webfont.woff2", "3", "application/octet-stream", 9)
            ],
            0);
        var output = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                Track("subtitles", "S_TEXT/ASS", true, false, "kor"),
                Track("subtitles", "S_TEXT/UTF8", false, false, "kor")
            ],
            [Attachment("cover.jpg", "2", "image/jpeg", 0)],
            0);

        Assert.Empty(MkvMergeClient.ValidateOutput(
            source,
            output,
            removeExistingFontAttachments: true));
    }

    [Fact]
    public async Task InspectionReadsDisplayMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-inspection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            const string inspectionJson = """
                {
                  "container":{"type":"Matroska","properties":{"duration":1441118000000}},
                  "tracks":[
                    {"id":0,"type":"video","codec":"HEVC/H.265/MPEG-H","properties":{"codec_id":"V_MPEGH/ISO/HEVC","pixel_dimensions":"1920x1080","default_duration":41708333,"tag_bps":"1182329"}},
                    {"id":1,"type":"audio","codec":"Opus","properties":{"codec_id":"A_OPUS","language":"jpn","language_ietf":"ja","audio_channels":2,"audio_sampling_frequency":48000,"tag_bps":"126567"}}
                  ],
                  "attachments":[],
                  "chapters":[]
                }
                """;

            var client = new MkvMergeClient("fake-mkvmerge.exe", new MuxArgumentRunner("unused", inspectionJson));
            var inspection = await client.InspectAsync(source);

            Assert.Equal("Matroska", inspection.ContainerType);
            Assert.Equal(1_441_118_000_000L, inspection.DurationNanoseconds);
            Assert.Equal(4L, inspection.FileSizeBytes);
            var video = Assert.Single(inspection.Tracks, static track => track.Type == "video");
            Assert.Equal("HEVC/H.265/MPEG-H", video.CodecName);
            Assert.Equal("1920x1080", video.PixelDimensions);
            Assert.Equal(41_708_333L, video.DefaultDurationNanoseconds);
            Assert.Equal(1_182_329L, video.Bitrate);
            var audio = Assert.Single(inspection.Tracks, static track => track.Type == "audio");
            Assert.Equal(2, audio.AudioChannels);
            Assert.Equal(48_000d, audio.AudioSamplingFrequency);
            Assert.Equal(126_567L, audio.Bitrate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("font/ttf", "payload.bin")]
    [InlineData("application/vnd.ms-opentype", "payload.bin")]
    [InlineData("application/octet-stream", "font.otc")]
    [InlineData("application/octet-stream", "font.woff2")]
    public void RecognizesFontAttachmentsByMimeOrExtension(string contentType, string fileName)
    {
        Assert.True(MkvMergeClient.IsFontAttachment(
            new MkvAttachmentInfo(fileName, contentType, null, 1, "1")));
    }

    [Fact]
    public void DoesNotTreatCoverArtAsAFont()
    {
        Assert.False(MkvMergeClient.IsFontAttachment(
            new MkvAttachmentInfo("cover.jpg", "image/jpeg", null, 1, "1")));
    }

    [Fact]
    public async Task MuxFontRemovalSelectsOnlyNonFontAttachments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-font-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[Script Info]\n[V4+ Styles]\n[Events]\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,x");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");

            const string inspection = """
                {"tracks":[
                  {"id":0,"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC"}}
                ],"attachments":[
                  {"id":3,"file_name":"font.ttf","content_type":"application/x-truetype-font","size":1,"properties":{"uid":10}},
                  {"id":7,"file_name":"cover.jpg","content_type":"image/jpeg","size":2,"properties":{"uid":11}}
                ],"chapters":[]}
                """;
            var runner = new MuxArgumentRunner(output, inspection);
            await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output,
                removeExistingFontAttachments: true);

            Assert.NotNull(runner.MuxArguments);
            var selectorIndex = runner.MuxArguments!.IndexOf("--attachments");
            Assert.True(selectorIndex >= 0);
            Assert.Equal("7", runner.MuxArguments[selectorIndex + 1]);
            Assert.True(selectorIndex < runner.MuxArguments.IndexOf(source));
            Assert.DoesNotContain("--no-attachments", runner.MuxArguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MuxFontRemovalUsesNoAttachmentsWhenEveryAttachmentIsAFont()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-font-only-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[Script Info]\n[V4+ Styles]\n[Events]\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,x");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");

            const string inspection = """
                {"tracks":[
                  {"id":0,"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC"}}
                ],"attachments":[
                  {"id":3,"file_name":"font.ttf","content_type":"application/x-truetype-font","size":1,"properties":{"uid":10}}
                ],"chapters":[]}
                """;
            var runner = new MuxArgumentRunner(output, inspection);
            await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output,
                removeExistingFontAttachments: true);

            Assert.NotNull(runner.MuxArguments);
            Assert.Contains("--no-attachments", runner.MuxArguments!);
            Assert.DoesNotContain("--attachments", runner.MuxArguments!);
            Assert.True(runner.MuxArguments.IndexOf("--no-attachments") < runner.MuxArguments.IndexOf(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MuxAddsNewFontWithExplicitNameAndMimeType()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-new-font-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var font = Path.Combine(root, "family-bold.otf");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[V4+ Styles]\nStyle: Default,Family,40\n[Events]");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");
            await File.WriteAllBytesAsync(font, [1, 2, 3, 4]);

            var runner = new MuxArgumentRunner(output);
            await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output,
                fontAttachments: [new FontAttachmentFile(font, "font/otf", "family-bold-a1b2c3d4.otf")]);

            Assert.NotNull(runner.MuxArguments);
            Assert.Contains("--attachment-mime-type", runner.MuxArguments!);
            Assert.Contains("font/otf", runner.MuxArguments!);
            Assert.Contains("--attachment-name", runner.MuxArguments!);
            Assert.Contains("family-bold-a1b2c3d4.otf", runner.MuxArguments!);
            Assert.Contains("--attach-file", runner.MuxArguments!);
            Assert.Contains(font, runner.MuxArguments!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MuxAddsGlobalTagsFileWhenRequested()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-global-tags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var tags = Path.Combine(root, "submux-tags.xml");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[V4+ Styles]\nStyle: Default,Family,40\n[Events]");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");
            await File.WriteAllTextAsync(tags, "<Tags />");

            var runner = new MuxArgumentRunner(output);
            await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output,
                globalTagsPath: tags);

            Assert.NotNull(runner.MuxArguments);
            var tagsIndex = runner.MuxArguments!.IndexOf("--global-tags");
            Assert.True(tagsIndex >= 0);
            Assert.Equal(tags, runner.MuxArguments[tagsIndex + 1]);
            Assert.True(tagsIndex < runner.MuxArguments.IndexOf(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MuxCanRemoveOldFontsAndAttachCurrentStyleFontTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-replace-font-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var font = Path.Combine(root, "current.ttf");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[V4+ Styles]\nStyle: Default,Family,40\n[Events]");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");
            await File.WriteAllBytesAsync(font, [1, 2, 3]);

            const string inspection = """
                {"tracks":[{"id":0,"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC"}}],
                 "attachments":[
                   {"id":3,"file_name":"old.ttf","content_type":"font/ttf","size":1,"properties":{"uid":10}},
                   {"id":7,"file_name":"cover.jpg","content_type":"image/jpeg","size":2,"properties":{"uid":11}}
                 ],"chapters":[]}
                """;
            var runner = new MuxArgumentRunner(output, inspection);
            await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output,
                removeExistingFontAttachments: true,
                fontAttachments: [new FontAttachmentFile(font, "font/ttf")]);

            Assert.NotNull(runner.MuxArguments);
            Assert.Contains("--attachments", runner.MuxArguments!);
            Assert.Contains("7", runner.MuxArguments!);
            Assert.Contains("--attach-file", runner.MuxArguments!);
            Assert.Contains(font, runner.MuxArguments!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MuxPreservationClearsEveryExistingSubtitleDefaultFlag()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-mux-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[Script Info]\n[V4+ Styles]\n[Events]\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,x");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");

            var runner = new MuxArgumentRunner(output);
            await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output,
                removeExistingSubtitles: false);

            Assert.NotNull(runner.MuxArguments);
            Assert.DoesNotContain("--no-subtitles", runner.MuxArguments!);
            Assert.Contains("0:스타일 자막 (ASS)", runner.MuxArguments!);
            Assert.Contains("0:일반 자막 (SRT)", runner.MuxArguments!);
            Assert.Contains("2:no", runner.MuxArguments!);
            Assert.Contains("5:no", runner.MuxArguments!);
            Assert.True(runner.MuxArguments!.IndexOf("2:no") < runner.MuxArguments.IndexOf(source));
            Assert.True(runner.MuxArguments.IndexOf("5:no") < runner.MuxArguments.IndexOf(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MkvTrackInfo Track(
        string type,
        string codec,
        bool isDefault = true,
        bool forced = false,
        string? language = null,
        string? trackName = null,
        int? id = null) =>
        new(type, codec, isDefault, forced, language, language, trackName, id);

    private static MkvAttachmentInfo Attachment(
        string name, string uid, string contentType = "application/x-truetype-font", int? id = null) =>
        new(name, contentType, null, 123, uid, id);

    private sealed class MuxArgumentRunner(string outputPath, string? inspectionJson = null) : IProcessRunner
    {
        public List<string>? MuxArguments { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            if (request.Arguments[0] == "-J")
            {
                const string defaultInspection = """
                    {"tracks":[
                      {"id":0,"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC"}},
                      {"id":2,"type":"subtitles","properties":{"codec_id":"S_TEXT/UTF8","default_track":true}},
                      {"id":5,"type":"subtitles","properties":{"codec_id":"S_HDMV/PGS","default_track":false}}
                    ],"attachments":[],"chapters":[]}
                    """;
                return Task.FromResult(new ProcessResult(0, inspectionJson ?? defaultInspection, string.Empty));
            }

            MuxArguments = request.Arguments.ToList();
            File.WriteAllBytes(outputPath, [1, 2, 3]);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
