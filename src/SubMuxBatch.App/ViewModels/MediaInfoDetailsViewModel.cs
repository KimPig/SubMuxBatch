using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using SubMuxBatch.App.Localization;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.App.ViewModels;

public sealed record MediaInfoDetailRow(string Label, string Value);

public sealed record MediaInfoDetailSection(
    string Title,
    IReadOnlyList<MediaInfoDetailRow> Rows,
    bool IsExpanded);

public sealed class MediaInfoDetailsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly QueueItemViewModel _source;
    private IReadOnlyList<MediaInfoDetailSection> _sections = [];
    private string _copyText = string.Empty;

    public MediaInfoDetailsViewModel(QueueItemViewModel source)
    {
        _source = source;
        _source.PropertyChanged += Source_PropertyChanged;
        Rebuild();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path => _source.MediaDetailsPath;
    public IReadOnlyList<MediaInfoDetailSection> Sections => _sections;
    public string CopyText => _copyText;

    public void Dispose() => _source.PropertyChanged -= Source_PropertyChanged;

    private void Source_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueueItemViewModel.MkvInspection)
            or nameof(QueueItemViewModel.DisplayInspection)
            or nameof(QueueItemViewModel.MediaDetailsPath))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var mediaInfo = _source.DisplayInspection;
        var mkvInfo = _source.MkvInspection;
        var sections = new List<MediaInfoDetailSection>();

        sections.Add(BuildGeneralSection(mediaInfo, mkvInfo));
        AddVideoSections(sections, mediaInfo, mkvInfo);
        AddAudioSections(sections, mediaInfo, mkvInfo);
        AddSubtitleSections(sections, mediaInfo, mkvInfo);
        sections.Add(BuildContentsSection(mediaInfo, mkvInfo));
        if (mkvInfo?.Attachments.Count > 0)
        {
            sections.Add(BuildAttachmentsSection(mkvInfo.Attachments));
        }

        _sections = sections;
        _copyText = BuildCopyText(sections);
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(CopyText));
    }

    private MediaInfoDetailSection BuildGeneralSection(
        MediaInfoInspection? mediaInfo,
        MkvInspection? mkvInfo)
    {
        var rows = new List<MediaInfoDetailRow>();
        var extension = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
        Add(rows, "MediaDetails_FieldContainer", mediaInfo?.ContainerFormat ?? mkvInfo?.ContainerType ?? extension);
        Add(rows, "MediaDetails_FieldContainerProfile", mediaInfo?.ContainerProfile);
        Add(rows, "MediaDetails_FieldContainerVersion", mediaInfo?.ContainerVersion);
        Add(rows, "MediaDetails_FieldDuration", FormatDuration(mediaInfo?.DurationNanoseconds ?? mkvInfo?.DurationNanoseconds));
        Add(rows, "MediaDetails_FieldFileSize", FormatFileSize(mediaInfo?.FileSizeBytes ?? mkvInfo?.FileSizeBytes));
        Add(rows, "MediaDetails_FieldOverallBitrate", FormatBitrate(mediaInfo?.OverallBitrate));
        Add(rows, "MediaDetails_FieldBitrateMode", mediaInfo?.OverallBitrateMode);
        Add(rows, "MediaDetails_FieldWritingApplication", mediaInfo?.WritingApplication);
        Add(rows, "MediaDetails_FieldWritingLibrary", mediaInfo?.WritingLibrary);
        Add(rows, "MediaDetails_FieldEncodedDate", mediaInfo?.EncodedDate);
        Add(rows, "MediaDetails_FieldTaggedDate", mediaInfo?.TaggedDate);
        EnsureNotEmpty(rows);
        return new MediaInfoDetailSection(AppText.Get("MediaDetails_General"), rows, true);
    }

    private static void AddVideoSections(
        ICollection<MediaInfoDetailSection> sections,
        MediaInfoInspection? mediaInfo,
        MkvInspection? mkvInfo)
    {
        var mediaStreams = mediaInfo?.VideoStreams ?? [];
        var mkvStreams = Tracks(mkvInfo, "video");
        var count = Math.Max(mediaStreams.Count, mkvStreams.Count);
        for (var index = 0; index < count; index++)
        {
            var stream = index < mediaStreams.Count ? mediaStreams[index] : null;
            var mkv = index < mkvStreams.Count ? mkvStreams[index] : null;
            var rows = new List<MediaInfoDetailRow>();
            Add(rows, "MediaDetails_FieldId", stream?.Id ?? FormatNullable(mkv?.Id));
            Add(rows, "MediaDetails_FieldStreamOrder", stream?.StreamOrder);
            Add(rows, "MediaDetails_FieldFormat", stream?.Format ?? mkv?.CodecName);
            Add(rows, "MediaDetails_FieldProfile", stream?.FormatProfile);
            Add(rows, "MediaDetails_FieldLevel", stream?.FormatLevel);
            Add(rows, "MediaDetails_FieldTier", stream?.FormatTier);
            Add(rows, "MediaDetails_FieldCodecId", stream?.CodecId ?? mkv?.CodecId);
            Add(rows, "MediaDetails_FieldTitle", stream?.Title ?? mkv?.TrackName);
            Add(rows, "MediaDetails_FieldLanguage", stream?.Language ?? mkv?.LanguageIetf ?? mkv?.Language);
            Add(rows, "MediaDetails_FieldResolution", FormatResolution(stream?.Width, stream?.Height, mkv?.PixelDimensions));
            Add(rows, "MediaDetails_FieldDisplayAspectRatio", FormatRatio(stream?.DisplayAspectRatio));
            Add(rows, "MediaDetails_FieldPixelAspectRatio", FormatRatio(stream?.PixelAspectRatio));
            Add(rows, "MediaDetails_FieldFrameRate", FormatFrameRate(stream?.FrameRate, mkv?.DefaultDurationNanoseconds));
            Add(rows, "MediaDetails_FieldFrameRateMode", stream?.FrameRateMode);
            Add(rows, "MediaDetails_FieldFrameCount", FormatCount(stream?.FrameCount));
            Add(rows, "MediaDetails_FieldDuration", FormatDuration(stream?.DurationNanoseconds));
            Add(rows, "MediaDetails_FieldBitrate", FormatBitrate(stream?.Bitrate ?? mkv?.Bitrate));
            Add(rows, "MediaDetails_FieldBitrateMode", stream?.BitrateMode);
            Add(rows, "MediaDetails_FieldMaximumBitrate", FormatBitrate(stream?.MaximumBitrate));
            Add(rows, "MediaDetails_FieldBitDepth", FormatBitDepth(stream?.BitDepth));
            Add(rows, "MediaDetails_FieldColorSpace", stream?.ColorSpace);
            Add(rows, "MediaDetails_FieldChromaSubsampling", stream?.ChromaSubsampling);
            Add(rows, "MediaDetails_FieldColorRange", stream?.ColorRange);
            Add(rows, "MediaDetails_FieldColorPrimaries", stream?.ColorPrimaries);
            Add(rows, "MediaDetails_FieldTransfer", stream?.TransferCharacteristics);
            Add(rows, "MediaDetails_FieldMatrix", stream?.MatrixCoefficients);
            Add(rows, "MediaDetails_FieldHdr", stream?.HdrFormat);
            Add(rows, "MediaDetails_FieldHdrCompatibility", stream?.HdrCompatibility);
            Add(rows, "MediaDetails_FieldScanType", stream?.ScanType);
            Add(rows, "MediaDetails_FieldScanOrder", stream?.ScanOrder);
            Add(rows, "MediaDetails_FieldDefault", FormatBoolean(stream?.Default ?? mkv?.DefaultTrack));
            Add(rows, "MediaDetails_FieldForced", FormatBoolean(stream?.Forced ?? mkv?.ForcedTrack));
            EnsureNotEmpty(rows);
            sections.Add(new MediaInfoDetailSection(
                AppText.Get("MediaDetails_TrackTitle", AppText.Get("MediaDetails_Video"), index + 1),
                rows,
                index == 0));
        }
    }

    private static void AddAudioSections(
        ICollection<MediaInfoDetailSection> sections,
        MediaInfoInspection? mediaInfo,
        MkvInspection? mkvInfo)
    {
        var mediaStreams = mediaInfo?.AudioStreams ?? [];
        var mkvStreams = Tracks(mkvInfo, "audio");
        var count = Math.Max(mediaStreams.Count, mkvStreams.Count);
        for (var index = 0; index < count; index++)
        {
            var stream = index < mediaStreams.Count ? mediaStreams[index] : null;
            var mkv = index < mkvStreams.Count ? mkvStreams[index] : null;
            var rows = new List<MediaInfoDetailRow>();
            Add(rows, "MediaDetails_FieldId", stream?.Id ?? FormatNullable(mkv?.Id));
            Add(rows, "MediaDetails_FieldStreamOrder", stream?.StreamOrder);
            Add(rows, "MediaDetails_FieldFormat", stream?.Format ?? mkv?.CodecName);
            Add(rows, "MediaDetails_FieldProfile", stream?.FormatProfile);
            Add(rows, "MediaDetails_FieldCodecId", stream?.CodecId ?? mkv?.CodecId);
            Add(rows, "MediaDetails_FieldTitle", stream?.Title ?? mkv?.TrackName);
            Add(rows, "MediaDetails_FieldLanguage", stream?.Language ?? mkv?.LanguageIetf ?? mkv?.Language);
            Add(rows, "MediaDetails_FieldChannels", FormatChannels(stream?.Channels ?? mkv?.AudioChannels));
            Add(rows, "MediaDetails_FieldChannelLayout", stream?.ChannelLayout);
            Add(rows, "MediaDetails_FieldSamplingRate", FormatSamplingRate(stream?.SamplingRate ?? mkv?.AudioSamplingFrequency));
            Add(rows, "MediaDetails_FieldDuration", FormatDuration(stream?.DurationNanoseconds));
            Add(rows, "MediaDetails_FieldBitrate", FormatBitrate(stream?.Bitrate ?? mkv?.Bitrate));
            Add(rows, "MediaDetails_FieldBitrateMode", stream?.BitrateMode);
            Add(rows, "MediaDetails_FieldMaximumBitrate", FormatBitrate(stream?.MaximumBitrate));
            Add(rows, "MediaDetails_FieldBitDepth", FormatBitDepth(stream?.BitDepth));
            Add(rows, "MediaDetails_FieldCompressionMode", stream?.CompressionMode);
            Add(rows, "MediaDetails_FieldDelay", FormatDelay(stream?.DelayMilliseconds));
            Add(rows, "MediaDetails_FieldDefault", FormatBoolean(stream?.Default ?? mkv?.DefaultTrack));
            Add(rows, "MediaDetails_FieldForced", FormatBoolean(stream?.Forced ?? mkv?.ForcedTrack));
            EnsureNotEmpty(rows);
            sections.Add(new MediaInfoDetailSection(
                AppText.Get("MediaDetails_TrackTitle", AppText.Get("MediaDetails_Audio"), index + 1),
                rows,
                index == 0));
        }
    }

    private static void AddSubtitleSections(
        ICollection<MediaInfoDetailSection> sections,
        MediaInfoInspection? mediaInfo,
        MkvInspection? mkvInfo)
    {
        var mediaStreams = mediaInfo?.TextStreams ?? [];
        var mkvStreams = Tracks(mkvInfo, "subtitles");
        var count = Math.Max(mediaStreams.Count, mkvStreams.Count);
        for (var index = 0; index < count; index++)
        {
            var stream = index < mediaStreams.Count ? mediaStreams[index] : null;
            var mkv = index < mkvStreams.Count ? mkvStreams[index] : null;
            var rows = new List<MediaInfoDetailRow>();
            Add(rows, "MediaDetails_FieldId", stream?.Id ?? FormatNullable(mkv?.Id));
            Add(rows, "MediaDetails_FieldStreamOrder", stream?.StreamOrder);
            Add(rows, "MediaDetails_FieldFormat", stream?.Format ?? mkv?.CodecName);
            Add(rows, "MediaDetails_FieldProfile", stream?.FormatProfile);
            Add(rows, "MediaDetails_FieldCodecId", stream?.CodecId ?? mkv?.CodecId);
            Add(rows, "MediaDetails_FieldTitle", stream?.Title ?? mkv?.TrackName);
            Add(rows, "MediaDetails_FieldLanguage", stream?.Language ?? mkv?.LanguageIetf ?? mkv?.Language);
            Add(rows, "MediaDetails_FieldDuration", FormatDuration(stream?.DurationNanoseconds));
            Add(rows, "MediaDetails_FieldElementCount", FormatCount(stream?.ElementCount));
            Add(rows, "MediaDetails_FieldDefault", FormatBoolean(stream?.Default ?? mkv?.DefaultTrack));
            Add(rows, "MediaDetails_FieldForced", FormatBoolean(stream?.Forced ?? mkv?.ForcedTrack));
            EnsureNotEmpty(rows);
            sections.Add(new MediaInfoDetailSection(
                AppText.Get("MediaDetails_TrackTitle", AppText.Get("MediaDetails_Subtitle"), index + 1),
                rows,
                false));
        }
    }

    private static MediaInfoDetailSection BuildContentsSection(
        MediaInfoInspection? mediaInfo,
        MkvInspection? mkvInfo)
    {
        var rows = new List<MediaInfoDetailRow>();
        Add(rows, "MediaDetails_FieldVideoTracks", CountText(Math.Max(mediaInfo?.VideoStreams.Count ?? 0, Tracks(mkvInfo, "video").Count)));
        Add(rows, "MediaDetails_FieldAudioTracks", CountText(Math.Max(mediaInfo?.AudioStreams.Count ?? 0, Tracks(mkvInfo, "audio").Count)));
        Add(rows, "MediaDetails_FieldSubtitleTracks", CountText(Math.Max(mediaInfo?.TextStreams.Count ?? 0, Tracks(mkvInfo, "subtitles").Count)));
        Add(rows, "MediaDetails_FieldAttachments", CountText(mkvInfo?.Attachments.Count ?? 0));
        Add(rows, "MediaDetails_FieldFontAttachments", CountText(mkvInfo?.Attachments.Count(MkvMergeClient.IsFontAttachment) ?? 0));
        Add(rows, "MediaDetails_FieldChapters", CountText(mkvInfo?.ChapterCount ?? mediaInfo?.MenuCount ?? 0));
        return new MediaInfoDetailSection(AppText.Get("MediaDetails_Structure"), rows, true);
    }

    private static MediaInfoDetailSection BuildAttachmentsSection(IReadOnlyList<MkvAttachmentInfo> attachments)
    {
        var rows = new List<MediaInfoDetailRow>();
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(attachment.ContentType)) details.Add(attachment.ContentType);
            if (attachment.Size is >= 0) details.Add(FormatFileSize(attachment.Size) ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(attachment.Description)) details.Add(attachment.Description);
            if (!string.IsNullOrWhiteSpace(attachment.Uid)) details.Add($"UID {attachment.Uid}");
            rows.Add(new MediaInfoDetailRow(
                attachment.FileName ?? AppText.Get("MediaDetails_AttachmentTitle", index + 1),
                details.Count > 0 ? string.Join(" · ", details) : AppText.Get("Common_Undetermined")));
        }

        return new MediaInfoDetailSection(AppText.Get("MediaDetails_Attachments"), rows, false);
    }

    private string BuildCopyText(IEnumerable<MediaInfoDetailSection> sections)
    {
        var builder = new StringBuilder();
        builder.AppendLine(AppText.Get("MediaDetails_Title", _source.Name));
        builder.AppendLine($"{AppText.Get("MediaDetails_Path")}: {Path}");
        foreach (var section in sections)
        {
            builder.AppendLine();
            builder.AppendLine($"[{section.Title}]");
            foreach (var row in section.Rows)
            {
                builder.AppendLine(string.IsNullOrWhiteSpace(row.Label)
                    ? row.Value
                    : $"{row.Label}: {row.Value}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<MkvTrackInfo> Tracks(MkvInspection? inspection, string type) =>
        inspection?.Tracks.Where(track => string.Equals(track.Type, type, StringComparison.OrdinalIgnoreCase)).ToArray()
        ?? [];

    private static void Add(ICollection<MediaInfoDetailRow> rows, string labelKey, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rows.Add(new MediaInfoDetailRow(AppText.Get(labelKey), value));
        }
    }

    private static void EnsureNotEmpty(ICollection<MediaInfoDetailRow> rows)
    {
        if (rows.Count == 0)
        {
            rows.Add(new MediaInfoDetailRow(string.Empty, AppText.Get("Common_Undetermined")));
        }
    }

    private static string? FormatNullable(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string CountText(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string? FormatDuration(long? nanoseconds)
    {
        if (nanoseconds is not > 0)
        {
            return null;
        }

        var span = TimeSpan.FromTicks(nanoseconds.Value / 100);
        var totalHours = (long)span.TotalHours;
        return $"{totalHours:00}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    private static string? FormatFileSize(long? bytes)
    {
        if (bytes is not >= 0)
        {
            return null;
        }

        var mib = bytes.Value / 1024d / 1024d;
        return $"{mib:N2} MiB ({bytes.Value:N0} bytes)";
    }

    private static string? FormatBitrate(long? bitrate)
    {
        if (bitrate is not > 0)
        {
            return null;
        }

        var shortValue = bitrate >= 1_000_000
            ? $"{bitrate.Value / 1_000_000d:0.###} Mbps"
            : $"{bitrate.Value / 1_000d:0.###} kbps";
        return $"{shortValue} ({bitrate.Value:N0} bps)";
    }

    private static string? FormatFrameRate(double? frameRate, long? defaultDuration)
    {
        var value = frameRate is > 0
            ? frameRate
            : defaultDuration is > 0 ? 1_000_000_000d / defaultDuration.Value : null;
        return value is > 0 ? $"{value.Value:0.###} fps" : null;
    }

    private static string? FormatResolution(int? width, int? height, string? fallback) =>
        width is > 0 && height is > 0 ? $"{width}×{height}" : fallback?.Replace('x', '×');

    private static string? FormatRatio(double? ratio) => ratio is > 0 ? ratio.Value.ToString("0.###", CultureInfo.InvariantCulture) : null;
    private static string? FormatCount(long? count) => count is > 0 ? count.Value.ToString("N0", CultureInfo.CurrentCulture) : null;
    private static string? FormatBitDepth(int? bitDepth) => bitDepth is > 0 ? $"{bitDepth}-bit" : null;
    private static string? FormatChannels(int? channels) => channels is > 0 ? $"{channels} ch" : null;
    private static string? FormatSamplingRate(double? samplingRate) => samplingRate is > 0 ? $"{samplingRate.Value / 1000d:0.###} kHz ({samplingRate.Value:N0} Hz)" : null;
    private static string? FormatDelay(double? milliseconds) => milliseconds is not null ? $"{milliseconds.Value:0.###} ms" : null;
    private static string FormatBoolean(bool? value) => value switch
    {
        true => AppText.Get("MediaDetails_Yes"),
        false => AppText.Get("MediaDetails_No"),
        null => string.Empty
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
