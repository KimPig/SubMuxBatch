using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class AudioConversionPlannerTests
{
    [Fact]
    public void DisabledConversionKeepsSelectedSourceTracks()
    {
        var source = Inspection(
            Audio(1, "A_OPUS", 2, "jpn", isDefault: true),
            Audio(2, "A_AAC", 2, "eng"));
        var settings = new AppSettings
        {
            FilterAudioTracksByLanguage = true,
            SelectedAudioLanguage = AudioTrackLanguage.Japanese
        };

        var plan = AudioConversionPlanner.Create(source, settings);

        Assert.Equal([1], plan.RetainedSourceTrackIds);
        Assert.Empty(plan.Transcodes);
    }

    [Fact]
    public void PreserveModeOnlyConvertsNonAacAndUsesChannelBasedBitrate()
    {
        var source = Inspection(
            Audio(1, "A_OPUS", 6, "jpn", isDefault: true),
            Audio(2, "A_AAC", 2, "jpn"));
        var settings = new AppSettings
        {
            ConvertAudioToAac = true,
            AudioChannelMode = AudioChannelMode.PreserveChannels
        };

        var plan = AudioConversionPlanner.Create(source, settings);

        Assert.Equal([2], plan.RetainedSourceTrackIds);
        var transcode = Assert.Single(plan.Transcodes);
        Assert.Equal(6, transcode.OutputChannels);
        Assert.Equal(384, transcode.BitrateKbps);
        Assert.True(transcode.DefaultTrack);
    }

    [Fact]
    public void StereoModeDownmixesMultichannelAndKeepsCompatibleAacStereo()
    {
        var source = Inspection(
            Audio(1, "A_DTS", 8, "jpn", isDefault: true),
            Audio(2, "A_AAC", 2, "eng"));
        var settings = new AppSettings
        {
            ConvertAudioToAac = true,
            AudioChannelMode = AudioChannelMode.ConvertToStereo
        };

        var plan = AudioConversionPlanner.Create(source, settings);

        Assert.Equal([2], plan.RetainedSourceTrackIds);
        var transcode = Assert.Single(plan.Transcodes);
        Assert.Equal(2, transcode.OutputChannels);
        Assert.Equal(192, transcode.BitrateKbps);
    }

    [Fact]
    public void KeepAndStereoModeKeepsMultichannelAndMakesStereoDefault()
    {
        var source = Inspection(Audio(7, "A_TRUEHD", 6, "jpn", isDefault: true));
        var settings = new AppSettings
        {
            ConvertAudioToAac = true,
            AudioChannelMode = AudioChannelMode.KeepMultichannelAndAddStereo
        };

        var plan = AudioConversionPlanner.Create(source, settings);

        Assert.Equal([7], plan.RetainedSourceTrackIds);
        Assert.False(plan.SourceDefaultTrackOverrides[7]);
        var stereo = Assert.Single(plan.Transcodes);
        Assert.Equal(2, stereo.OutputChannels);
        Assert.True(stereo.DefaultTrack);
    }

    [Fact]
    public void KeepAndStereoModeReusesExistingAacStereoOfSameLanguage()
    {
        var source = Inspection(
            Audio(1, "A_FLAC", 6, "jpn", isDefault: true),
            Audio(2, "A_AAC", 2, "jpn"));
        var settings = new AppSettings
        {
            ConvertAudioToAac = true,
            AudioChannelMode = AudioChannelMode.KeepMultichannelAndAddStereo
        };

        var plan = AudioConversionPlanner.Create(source, settings);

        Assert.Equal([1, 2], plan.RetainedSourceTrackIds.Order());
        Assert.Empty(plan.Transcodes);
        Assert.False(plan.SourceDefaultTrackOverrides[1]);
        Assert.True(plan.SourceDefaultTrackOverrides[2]);
    }

    [Fact]
    public void KeepAndStereoModeDoesNotDuplicatePlannedStereoReplacement()
    {
        var source = Inspection(
            Audio(1, "A_TRUEHD", 6, "jpn", isDefault: true),
            Audio(2, "A_OPUS", 2, "jpn"));
        var settings = new AppSettings
        {
            ConvertAudioToAac = true,
            AudioChannelMode = AudioChannelMode.KeepMultichannelAndAddStereo
        };

        var plan = AudioConversionPlanner.Create(source, settings);

        Assert.Equal([1], plan.RetainedSourceTrackIds);
        var stereo = Assert.Single(plan.Transcodes);
        Assert.Equal(2, stereo.SourceTrack.Id);
        Assert.Equal(2, stereo.OutputChannels);
        Assert.True(stereo.DefaultTrack);
        Assert.False(plan.SourceDefaultTrackOverrides[1]);
    }

    [Fact]
    public void KeepAndStereoModeCreatesACompanionForEachDistinctMultichannelTrack()
    {
        var first = Audio(1, "A_TRUEHD", 6, "jpn", isDefault: true) with { TrackName = "Main" };
        var second = Audio(2, "A_DTS", 6, "jpn") with { TrackName = "Commentary" };
        var source = Inspection(first, second);
        var settings = new AppSettings
        {
            ConvertAudioToAac = true,
            AudioChannelMode = AudioChannelMode.KeepMultichannelAndAddStereo
        };

        var plan = AudioConversionPlanner.Create(source, settings);

        Assert.Equal([1, 2], plan.RetainedSourceTrackIds.Order());
        Assert.Equal(2, plan.Transcodes.Count);
        Assert.Contains(plan.Transcodes, static track => track.SourceTrack.TrackName == "Main" && track.OutputChannels == 2);
        Assert.Contains(plan.Transcodes, static track => track.SourceTrack.TrackName == "Commentary" && track.OutputChannels == 2);
    }

    [Fact]
    public void MissingFilteredLanguageSkipsInsteadOfCreatingSilentOutput()
    {
        var source = Inspection(
            Audio(1, "A_AAC", 2, "und"),
            Audio(2, "A_AAC", 2, "eng"));
        var settings = new AppSettings
        {
            ConvertAudioToAac = true,
            FilterAudioTracksByLanguage = true,
            SelectedAudioLanguage = AudioTrackLanguage.Japanese
        };

        Assert.Throws<JobSkippedException>(() => AudioConversionPlanner.Create(source, settings));
    }

    private static MkvInspection Inspection(params MkvTrackInfo[] audioTracks) => new(
        [new MkvTrackInfo("video", "V_MPEGH/ISO/HEVC", true, false, null, null, null, 0), .. audioTracks],
        [],
        0,
        "Matroska",
        60_000_000_000);

    private static MkvTrackInfo Audio(
        int id,
        string codec,
        int channels,
        string language,
        bool isDefault = false) => new(
        "audio",
        codec,
        isDefault,
        false,
        language,
        null,
        null,
        id,
        AudioChannels: channels,
        AudioSamplingFrequency: 48_000);
}
