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
