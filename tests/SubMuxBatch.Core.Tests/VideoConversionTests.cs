using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class VideoConversionTests
{
    [Fact]
    public void NonHevcModeConvertsAv1ButKeepsHevc()
    {
        var settings = new AppSettings { VideoProcessingMode = VideoProcessingMode.ConvertNonHevc };
        var av1 = Inspect("V_AV1");
        var hevc = Inspect("V_MPEGH/ISO/HEVC");

        Assert.NotNull(VideoConversionPlanner.Create(av1, settings));
        Assert.Null(VideoConversionPlanner.Create(hevc, settings));
    }

    [Fact]
    public void ReencodeAllConvertsExistingHevc()
    {
        var settings = new AppSettings { VideoProcessingMode = VideoProcessingMode.ReencodeAll };

        Assert.NotNull(VideoConversionPlanner.Create(Inspect("V_MPEGH/ISO/HEVC"), settings));
    }

    [Theory]
    [InlineData(VideoQualityProfile.Fast, "faster", 24)]
    [InlineData(VideoQualityProfile.Balanced, "medium", 23)]
    [InlineData(VideoQualityProfile.HighQuality, "medium", 21)]
    public void BuiltInProfilesResolveToExpectedX265Settings(
        VideoQualityProfile profile,
        string expectedPreset,
        int expectedCrf)
    {
        var request = new VideoTranscodeRequest(
            "source.mkv", 0, "output.mkv", profile,
            X265Preset.Slow, VideoRateControlMode.AverageBitrate, 17, 4500,
            X265Tune.Animation, VideoCpuUsageMode.Auto, 2, "aq-mode=3", null);

        var actual = BundledFfmpegVideoTranscoder.ResolveEncodingSettings(request);

        Assert.Equal(expectedPreset, actual.Preset);
        Assert.Equal(expectedCrf, actual.Crf);
        Assert.Equal(VideoRateControlMode.ConstantQuality, actual.RateControl);
        Assert.Equal(X265Tune.None, actual.Tune);
    }

    [Fact]
    public void CustomProfileUsesEveryCustomValue()
    {
        var request = new VideoTranscodeRequest(
            "source.mkv", 0, "output.mkv", VideoQualityProfile.Custom,
            X265Preset.Slow, VideoRateControlMode.TwoPassAverageBitrate, 20, 4500,
            X265Tune.Animation, VideoCpuUsageMode.Custom, 3, "aq-mode=3", null);

        var actual = BundledFfmpegVideoTranscoder.ResolveEncodingSettings(request);
        Assert.Equal("slow", actual.Preset);
        Assert.Equal(VideoRateControlMode.TwoPassAverageBitrate, actual.RateControl);
        Assert.Equal(20, actual.Crf);
        Assert.Equal(4500, actual.BitrateKbps);
        Assert.Equal(X265Tune.Animation, actual.Tune);
        Assert.Equal(3, actual.ThreadCount);
    }

    [Fact]
    public void SettingsCopyAndPresetKeepVideoAndAudioEncodingOptions()
    {
        var settings = new AppSettings
        {
            VideoProcessingMode = VideoProcessingMode.ReencodeAll,
            VideoQualityProfile = VideoQualityProfile.Custom,
            CustomX265Preset = X265Preset.Fast,
            CustomVideoRateControl = VideoRateControlMode.AverageBitrate,
            CustomX265Crf = 22,
            CustomVideoBitrateKbps = 3500,
            CustomX265Tune = X265Tune.Animation,
            VideoCpuUsage = VideoCpuUsageMode.Custom,
            CustomVideoThreadCount = 4,
            CustomX265Parameters = "aq-mode=3",
            AudioProcessingMode = AudioProcessingMode.ReencodeAll,
            AudioCodec = AudioCodec.Opus,
            AudioBitrateKbps = 160,
            MaintenanceApplyVideoSettings = false
        };

        var copy = settings.Copy();
        var applied = new AppSettings();
        ProcessingPresetSettings.Capture(settings).ApplyTo(applied);

        Assert.Equal(settings.VideoProcessingMode, copy.VideoProcessingMode);
        Assert.Equal(settings.CustomX265Parameters, copy.CustomX265Parameters);
        Assert.Equal(settings.CustomVideoRateControl, copy.CustomVideoRateControl);
        Assert.Equal(settings.VideoCpuUsage, copy.VideoCpuUsage);
        Assert.Equal(settings.AudioCodec, copy.AudioCodec);
        Assert.Equal(settings.AudioBitrateKbps, copy.AudioBitrateKbps);
        Assert.Equal(settings.VideoProcessingMode, applied.VideoProcessingMode);
        Assert.Equal(settings.VideoQualityProfile, applied.VideoQualityProfile);
        Assert.Equal(settings.CustomX265Crf, applied.CustomX265Crf);
        Assert.Equal(settings.CustomVideoBitrateKbps, applied.CustomVideoBitrateKbps);
        Assert.Equal(settings.CustomVideoThreadCount, applied.CustomVideoThreadCount);
        Assert.Equal(settings.AudioProcessingMode, applied.AudioProcessingMode);
        Assert.Equal(settings.AudioCodec, applied.AudioCodec);
        Assert.False(applied.MaintenanceApplyVideoSettings);
    }

    [Fact]
    public void LegacyPresetAudioFlagNormalizesToEffectiveMode()
    {
        var preset = new ProcessingPresetSettings
        {
            ConvertAudioToAac = true,
            AudioProcessingMode = null
        };

        preset.NormalizeLegacyValues();

        Assert.Equal(AudioProcessingMode.ConvertWhenNeeded, preset.AudioProcessingMode);
        Assert.False(preset.ConvertAudioToAac);
        var settings = new AppSettings();
        preset.ApplyTo(settings);
        Assert.Equal(AudioProcessingMode.ConvertWhenNeeded, settings.AudioProcessingMode);
    }

    [Fact]
    public void VideoMetadataValidationPreservesDisplayDimensions()
    {
        var source = new MkvTrackInfo(
            "video", "V_MPEG4/ISO/AVC", true, false, null, null, null,
            PixelDimensions: "1920x800", DefaultDurationNanoseconds: 41_708_333,
            DisplayDimensions: "1920x1080", FrameCount: 34_560);
        var matching = source with { CodecId = "V_MPEGH/ISO/HEVC" };
        var changed = matching with { DisplayDimensions = "1920x800" };

        Assert.True(MkvMergeClient.VideoTechnicalMetadataPreserved(source, matching));
        Assert.False(MkvMergeClient.VideoTechnicalMetadataPreserved(source, changed));
        Assert.False(MkvMergeClient.VideoTechnicalMetadataPreserved(
            source,
            matching with { FrameCount = 34_559 }));
    }

    private static MkvInspection Inspect(string codecId) => new(
        [new MkvTrackInfo("video", codecId, true, false, null, null, null, 0)],
        [],
        0);
}
