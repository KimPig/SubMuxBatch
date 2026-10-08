using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Fonts;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Processing;

internal static class ProcessingDecisionFormatter
{
    public static IReadOnlyList<string> DescribeAudioPlan(
        MkvInspection source,
        AudioConversionPlan plan)
    {
        var messages = new List<string>();
        foreach (var track in source.Tracks.Where(static track =>
                     string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase)))
        {
            var selected = plan.SelectedSourceTracks.Any(candidate => SameTrack(candidate, track));
            if (!selected)
            {
                messages.Add(CoreText.Get("Decision_AudioExcluded", DescribeTrack(track)));
                continue;
            }

            if (track.Id is not null && plan.RetainedSourceTrackIds.Contains(track.Id.Value))
            {
                messages.Add(CoreText.Get("Decision_AudioRetained", DescribeTrack(track)));
            }

            foreach (var transcode in plan.Transcodes.Where(candidate => SameTrack(candidate.SourceTrack, track)))
            {
                messages.Add(CoreText.Get(
                    "Decision_AudioConverted",
                    DescribeTrack(track),
                    transcode.Codec == AudioCodec.Opus ? "Opus" : "AAC-LC",
                    transcode.OutputChannels?.ToString() ?? track.AudioChannels?.ToString() ?? "?",
                    transcode.BitrateKbps));
            }
        }

        return messages;
    }

    public static IReadOnlyList<string> DescribeUnchangedAudio(MkvInspection source) =>
        source.Tracks
            .Where(static track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase))
            .Select(track => CoreText.Get("Decision_AudioRetained", DescribeTrack(track)))
            .ToArray();

    public static string DescribeVideoPlan(MkvTrackInfo source, AppSettings settings)
    {
        var codec = string.IsNullOrWhiteSpace(source.CodecName) ? source.CodecId : source.CodecName;
        var profile = settings.VideoQualityProfile switch
        {
            VideoQualityProfile.Fast => "faster / CRF 24",
            VideoQualityProfile.Balanced => "medium / CRF 23",
            VideoQualityProfile.HighQuality => "medium / CRF 21",
            VideoQualityProfile.Custom => settings.CustomVideoRateControl switch
            {
                VideoRateControlMode.ConstantQuality =>
                    $"{settings.CustomX265Preset.ToString().ToLowerInvariant()} / CRF {settings.CustomX265Crf}",
                VideoRateControlMode.AverageBitrate =>
                    $"{settings.CustomX265Preset.ToString().ToLowerInvariant()} / {settings.CustomVideoBitrateKbps} kbps / 1-pass",
                VideoRateControlMode.TwoPassAverageBitrate =>
                    $"{settings.CustomX265Preset.ToString().ToLowerInvariant()} / {settings.CustomVideoBitrateKbps} kbps / 2-pass",
                _ => throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoEncoding"))
            },
            _ => throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoEncoding"))
        };
        var cpu = settings.VideoCpuUsage switch
        {
            VideoCpuUsageMode.Auto => CoreText.Get("Decision_VideoCpuAuto"),
            VideoCpuUsageMode.Low => CoreText.Get("Decision_VideoCpuLow"),
            VideoCpuUsageMode.Normal => CoreText.Get("Decision_VideoCpuNormal"),
            VideoCpuUsageMode.Maximum => CoreText.Get("Decision_VideoCpuMaximum"),
            VideoCpuUsageMode.Custom => CoreText.Get("Decision_VideoCpuThreads", settings.CustomVideoThreadCount),
            _ => throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoEncoding"))
        };
        return CoreText.Get("Decision_VideoConverted", source.Id ?? 0, codec, profile, cpu);
    }

    public static string DescribeFontAttachments(IReadOnlyList<FontAttachmentFile> attachments) =>
        attachments.Count == 0
            ? CoreText.Get("Decision_NoFontAttachments")
            : CoreText.Get(
                "Decision_FontAttachments",
                string.Join(", ", attachments.Select(static attachment => attachment.FileName)));

    public static string DescribeVerifiedOutput(MkvInspection inspection)
    {
        var video = inspection.Tracks.Count(static track =>
            string.Equals(track.Type, "video", StringComparison.OrdinalIgnoreCase));
        var audio = inspection.Tracks.Count(static track =>
            string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase));
        var subtitles = inspection.Tracks.Count(static track =>
            string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase));
        var fonts = inspection.Attachments.Count(MkvMergeClient.IsFontAttachment);
        return CoreText.Get(
            "Decision_VerifiedOutput",
            video,
            audio,
            subtitles,
            fonts,
            inspection.ChapterCount ?? 0);
    }

    private static bool SameTrack(MkvTrackInfo left, MkvTrackInfo right) =>
        left.Id is not null && right.Id is not null
            ? left.Id == right.Id
            : ReferenceEquals(left, right) || left == right;

    private static string DescribeTrack(MkvTrackInfo track)
    {
        var id = track.Id?.ToString() ?? "?";
        var codec = string.IsNullOrWhiteSpace(track.CodecName) ? track.CodecId : track.CodecName;
        var language = !string.IsNullOrWhiteSpace(track.LanguageIetf)
            ? track.LanguageIetf
            : !string.IsNullOrWhiteSpace(track.Language) ? track.Language : "und";
        var channels = track.AudioChannels?.ToString() ?? "?";
        return CoreText.Get("Decision_AudioTrack", id, codec, language, channels);
    }
}
