using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class AudioLanguageFilterTests
{
    [Fact]
    public void NewAndLegacySettingsLeaveAudioFilteringDisabled()
    {
        var fresh = new AppSettings();
        var legacy = AppSettings.Deserialize("{}");

        Assert.False(fresh.FilterAudioTracksByLanguage);
        Assert.Equal(AudioTrackLanguage.Japanese, fresh.SelectedAudioLanguage);
        Assert.False(legacy.FilterAudioTracksByLanguage);
        Assert.Equal(AudioTrackLanguage.Japanese, legacy.SelectedAudioLanguage);
    }

    [Fact]
    public void SettingsCopyPreservesAudioLanguageSelection()
    {
        var settings = new AppSettings
        {
            FilterAudioTracksByLanguage = true,
            SelectedAudioLanguage = AudioTrackLanguage.Korean
        };

        var copy = settings.Copy();

        Assert.True(copy.FilterAudioTracksByLanguage);
        Assert.Equal(AudioTrackLanguage.Korean, copy.SelectedAudioLanguage);
    }

    [Fact]
    public void EnabledFilterRejectsAnUnknownLanguageValue()
    {
        var settings = new AppSettings
        {
            FilterAudioTracksByLanguage = true,
            SelectedAudioLanguage = (AudioTrackLanguage)99
        };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public async Task MuxKeepsAllMatchingTracksAndPlacesSelectionBeforeSource()
    {
        using var fixture = new MuxFixture();
        const string inspection = """
            {"tracks":[
              {"id":0,"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC"}},
              {"id":1,"type":"audio","properties":{"codec_id":"A_AAC","default_track":true,"language":"eng","language_ietf":"en"}},
              {"id":2,"type":"audio","properties":{"codec_id":"A_AAC","default_track":false,"language":"jpn","language_ietf":"ja"}},
              {"id":4,"type":"audio","properties":{"codec_id":"A_OPUS","default_track":false,"language":"eng","language_ietf":"ja-JP"}},
              {"id":5,"type":"audio","properties":{"codec_id":"A_AAC","default_track":false,"language":"jpn","language_ietf":"en-US"}}
            ],"attachments":[],"chapters":[]}
            """;
        var runner = new MuxArgumentRunner(fixture.Output, inspection);

        await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
            fixture.Source,
            fixture.Ass,
            fixture.Srt,
            fixture.Output,
            keepOnlyAudioLanguage: AudioTrackLanguage.Japanese);

        Assert.NotNull(runner.MuxArguments);
        var selectorIndex = runner.MuxArguments!.IndexOf("--audio-tracks");
        Assert.True(selectorIndex >= 0);
        Assert.Equal("2,4", runner.MuxArguments[selectorIndex + 1]);
        Assert.True(selectorIndex < runner.MuxArguments.IndexOf(fixture.Source));
        Assert.DoesNotContain("--no-audio", runner.MuxArguments);
        Assert.Contains("2:yes", runner.MuxArguments);
        Assert.DoesNotContain("5:yes", runner.MuxArguments);
    }

    [Fact]
    public async Task FilterDoesNotRemoveTheOnlyAudioTrack()
    {
        using var fixture = new MuxFixture();
        const string inspection = """
            {"tracks":[
              {"id":0,"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC"}},
              {"id":1,"type":"audio","properties":{"codec_id":"A_AAC","default_track":true,"language":"eng","language_ietf":"en"}}
            ],"attachments":[],"chapters":[]}
            """;
        var runner = new MuxArgumentRunner(fixture.Output, inspection);

        await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
            fixture.Source,
            fixture.Ass,
            fixture.Srt,
            fixture.Output,
            keepOnlyAudioLanguage: AudioTrackLanguage.Japanese);

        Assert.NotNull(runner.MuxArguments);
        Assert.DoesNotContain("--audio-tracks", runner.MuxArguments!);
        Assert.DoesNotContain("--no-audio", runner.MuxArguments!);
    }

    [Fact]
    public async Task FilterSkipsInsteadOfCreatingSilentOutputWhenNoLanguageMatches()
    {
        using var fixture = new MuxFixture();
        const string inspection = """
            {"tracks":[
              {"id":0,"type":"video","properties":{"codec_id":"V_MPEGH/ISO/HEVC"}},
              {"id":1,"type":"audio","properties":{"codec_id":"A_AAC","language":"eng","language_ietf":"en"}},
              {"id":2,"type":"audio","properties":{"codec_id":"A_AAC","language":"kor","language_ietf":"ko"}}
            ],"attachments":[],"chapters":[]}
            """;
        var runner = new MuxArgumentRunner(fixture.Output, inspection);

        var exception = await Assert.ThrowsAsync<JobSkippedException>(() =>
            new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                fixture.Source,
                fixture.Ass,
                fixture.Srt,
                fixture.Output,
                keepOnlyAudioLanguage: AudioTrackLanguage.Japanese));

        Assert.Contains("원본 영상", exception.Message);
        Assert.Contains("해당 작업은 건너뜁니다.", exception.Message);
        Assert.Null(runner.MuxArguments);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact]
    public void ValidationAcceptsOnlyTheSelectedAudioLanguage()
    {
        var source = new MkvInspection(
        [
            Track("video", "V_MPEGH/ISO/HEVC"),
            Track("audio", "A_AAC", true, "eng", "en", 1),
            Track("audio", "A_AAC", false, "jpn", "ja", 2),
            Track("audio", "A_OPUS", false, "jpn", "ja-JP", 4),
        ],
        [],
        0);
        var output = new MkvInspection(
        [
            Track("video", "V_MPEGH/ISO/HEVC"),
            Track("audio", "A_AAC", true, "jpn", "ja", 1),
            Track("audio", "A_OPUS", false, "jpn", "ja-JP", 2),
            Track("subtitles", "S_TEXT/ASS", true, "kor", "ko", 3),
            Track("subtitles", "S_TEXT/UTF8", false, "kor", "ko", 4),
        ],
        [],
        0);

        var errors = MkvMergeClient.ValidateOutput(
            source,
            output,
            keepOnlyAudioLanguage: AudioTrackLanguage.Japanese);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidationRejectsWrongLanguageEvenWhenCountAndCodecMatch()
    {
        var source = new MkvInspection(
        [
            Track("video", "V_MPEGH/ISO/HEVC"),
            Track("audio", "A_AAC", true, "eng", "en", 1),
            Track("audio", "A_AAC", false, "jpn", "ja", 2),
        ],
        [],
        0);
        var output = new MkvInspection(
        [
            Track("video", "V_MPEGH/ISO/HEVC"),
            Track("audio", "A_AAC", true, "eng", "en", 1),
            Track("subtitles", "S_TEXT/ASS", true, "kor", "ko", 2),
            Track("subtitles", "S_TEXT/UTF8", false, "kor", "ko", 3),
        ],
        [],
        0);

        var errors = MkvMergeClient.ValidateOutput(
            source,
            output,
            keepOnlyAudioLanguage: AudioTrackLanguage.Japanese);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ValidationRejectsSelectedAudioMetadataChanges()
    {
        var source = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                new MkvTrackInfo("audio", "A_AAC", false, false, "jpn", "ja", "Main", 2),
                new MkvTrackInfo("audio", "A_AAC", false, true, "jpn", "ja", "Commentary", 4),
            ],
            [],
            0);
        var output = new MkvInspection(
            [
                Track("video", "V_MPEGH/ISO/HEVC"),
                new MkvTrackInfo("audio", "A_AAC", true, false, "jpn", "ja", "Main", 1),
                new MkvTrackInfo("audio", "A_AAC", false, false, "jpn", "ja", "Wrong name", 2),
                Track("subtitles", "S_TEXT/ASS", true, "kor", "ko", 3),
                Track("subtitles", "S_TEXT/UTF8", false, "kor", "ko", 4),
            ],
            [],
            0);

        var errors = MkvMergeClient.ValidateOutput(
            source,
            output,
            keepOnlyAudioLanguage: AudioTrackLanguage.Japanese);

        Assert.NotEmpty(errors);
    }

    private static MkvTrackInfo Track(
        string type,
        string codec,
        bool isDefault = true,
        string? language = null,
        string? languageIetf = null,
        int? id = null) =>
        new(type, codec, isDefault, false, language, languageIetf, null, id);

    private sealed class MuxFixture : IDisposable
    {
        public MuxFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"submux-batch-audio-filter-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Source = Path.Combine(Root, "source.mkv");
            Ass = Path.Combine(Root, "new.ass");
            Srt = Path.Combine(Root, "new.srt");
            Output = Path.Combine(Root, "output.mkv");
            File.WriteAllBytes(Source, [1]);
            File.WriteAllText(Ass, "[Script Info]\n[V4+ Styles]\n[Events]\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,x");
            File.WriteAllText(Srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");
        }

        public string Root { get; }
        public string Source { get; }
        public string Ass { get; }
        public string Srt { get; }
        public string Output { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class MuxArgumentRunner(string outputPath, string inspectionJson) : IProcessRunner
    {
        public List<string>? MuxArguments { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            if (request.Arguments[0] == "-J")
            {
                return Task.FromResult(new ProcessResult(0, inspectionJson, string.Empty));
            }

            MuxArguments = request.Arguments.ToList();
            File.WriteAllBytes(outputPath, [1, 2, 3]);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
