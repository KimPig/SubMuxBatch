using System.ComponentModel;
using System.Globalization;
using System.IO;
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

public sealed record MediaSizeDisplayRow(
    string Category,
    string Name,
    string Details,
    string Size,
    string Share,
    string Basis,
    string? SizeToolTip = null);

public sealed class MediaInfoDetailsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly QueueItemViewModel _source;
    private IReadOnlyList<MediaInfoDetailSection> _sections = [];
    private IReadOnlyList<MediaSizeDisplayRow> _sizeRows = [];
    private string _copyText = string.Empty;
    private bool _isProcessedBySubMux;
    private bool _isCalculatingSizes;
    private bool _canCalculateExactSizes;
    private bool _hasInexactTrackSizes;
    private string _sizeStatusText = string.Empty;
    private string _totalFileSizeText = string.Empty;
    private string _videoSizeText = string.Empty;
    private string _audioSizeText = string.Empty;
    private string _subtitleAttachmentSizeText = string.Empty;
    private string _containerOverheadText = string.Empty;
    private MediaInfoStreamSizeReport? _fullSizeReport;
    private string? _fullSizeReportPath;
    private CancellationTokenSource? _sizeCancellation;

    public MediaInfoDetailsViewModel(QueueItemViewModel source)
    {
        _source = source;
        _source.PropertyChanged += Source_PropertyChanged;
        Rebuild();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path => _source.MediaDetailsPath;
    public IReadOnlyList<MediaInfoDetailSection> Sections => _sections;
    public IReadOnlyList<MediaSizeDisplayRow> SizeRows => _sizeRows;
    public string CopyText => _copyText;
    public bool IsProcessedBySubMux => _isProcessedBySubMux;
    public bool IsCalculatingSizes => _isCalculatingSizes;
    public bool CanCalculateExactSizes => _canCalculateExactSizes;
    public string SizeStatusText => _sizeStatusText;
    public string TotalFileSizeText => _totalFileSizeText;
    public string VideoSizeText => _videoSizeText;
    public string AudioSizeText => _audioSizeText;
    public string SubtitleAttachmentSizeText => _subtitleAttachmentSizeText;
    public string ContainerOverheadText => _containerOverheadText;

    public void Dispose()
    {
        _source.PropertyChanged -= Source_PropertyChanged;
        _sizeCancellation?.Cancel();
        _sizeCancellation?.Dispose();
    }

    public async Task CalculateExactSizesAsync()
    {
        if (_isCalculatingSizes || !_canCalculateExactSizes || !File.Exists(Path))
        {
            return;
        }

        _sizeCancellation?.Cancel();
        _sizeCancellation?.Dispose();
        _sizeCancellation = new CancellationTokenSource();
        _isCalculatingSizes = true;
        _canCalculateExactSizes = false;
        _sizeStatusText = AppText.Get("MediaSizes_Calculating");
        RaiseSizeProperties();

        try
        {
            var ffmpegPath = File.Exists(BundledFfmpegProvider.ExecutablePath)
                ? BundledFfmpegProvider.ExecutablePath
                : await new BundledFfmpegProvider().GetExecutablePathAsync(_sizeCancellation.Token);
            _fullSizeReport = await new FfmpegPacketSizeAnalyzer(ffmpegPath).AnalyzeAsync(
                Path,
                _sizeCancellation.Token);
            _fullSizeReportPath = Path;
            RebuildSizes();
        }
        catch (OperationCanceledException)
        {
            _sizeStatusText = AppText.Get("MediaSizes_Cancelled");
        }
        catch (Exception exception)
        {
            _sizeStatusText = AppText.Get("MediaSizes_Failed", exception.Message);
        }
        finally
        {
            _isCalculatingSizes = false;
            _canCalculateExactSizes = _hasInexactTrackSizes;
            RaiseSizeProperties();
        }
    }

    private void Source_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueueItemViewModel.MkvInspection)
            or nameof(QueueItemViewModel.DisplayInspection)
            or nameof(QueueItemViewModel.MediaDetailsPath))
        {
            if (e.PropertyName == nameof(QueueItemViewModel.MediaDetailsPath)
                && !string.Equals(_fullSizeReportPath, Path, StringComparison.OrdinalIgnoreCase))
            {
                _fullSizeReport = null;
                _fullSizeReportPath = null;
            }
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var mediaInfo = _source.DisplayInspection;
        var mkvInfo = _source.MkvInspection;
        var sections = new List<MediaInfoDetailSection>();

        _isProcessedBySubMux = mediaInfo?.ProcessedBySubMux == true;
        sections.Add(BuildGeneralSection(mediaInfo, mkvInfo));
        AddVideoSections(sections, mediaInfo, mkvInfo);
        AddAudioSections(sections, mediaInfo, mkvInfo);
        AddSubtitleSections(sections, mediaInfo, mkvInfo);
        sections.Add(BuildContentsSection(mediaInfo, mkvInfo));
        if (mkvInfo?.Attachments.Count > 0)
        {
            sections.Add(BuildAttachmentsSection(mkvInfo.Attachments));
        }
        if (mediaInfo?.MetadataTags.Count > 0)
        {
            sections.Add(BuildMetadataTagsSection(mediaInfo.MetadataTags));
        }
        if (_isProcessedBySubMux)
        {
            sections.Add(BuildSubMuxTagsSection(mediaInfo!));
        }

        _sections = sections;
        RebuildSizes();
        _copyText = BuildCopyText(sections, _isProcessedBySubMux) + BuildSizeCopyText();
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(CopyText));
        OnPropertyChanged(nameof(IsProcessedBySubMux));
    }

    private static MediaInfoDetailSection BuildMetadataTagsSection(
        IReadOnlyList<MediaInfoMetadataTag> tags) =>
        new(
            AppText.Get("MediaDetails_MetadataTags"),
            tags.Select(static tag => new MediaInfoDetailRow(tag.Name, tag.Value)).ToArray(),
            false);

    private static MediaInfoDetailSection BuildSubMuxTagsSection(MediaInfoInspection mediaInfo)
    {
        var rows = new List<MediaInfoDetailRow>();
        if (!string.IsNullOrWhiteSpace(mediaInfo.SubMuxBatchVersion))
        {
            rows.Add(new MediaInfoDetailRow(
                SubMuxMetadata.VersionTagName,
                mediaInfo.SubMuxBatchVersion));
        }

        if (!string.IsNullOrWhiteSpace(mediaInfo.SubMuxProcessedMarker))
        {
            rows.Add(new MediaInfoDetailRow(
                SubMuxMetadata.ProcessedTagName,
                mediaInfo.SubMuxProcessedMarker));
        }
        else if (mediaInfo.Comment?.Contains(
                     SubMuxMetadata.ProcessedValue,
                     StringComparison.OrdinalIgnoreCase) == true)
        {
            rows.Add(new MediaInfoDetailRow(
                SubMuxMetadata.LegacyCommentTagName,
                mediaInfo.Comment));
        }

        EnsureNotEmpty(rows);
        return new MediaInfoDetailSection(AppText.Get("MediaDetails_SubMuxTags"), rows, true);
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
        Add(rows, "MediaDetails_FieldOverallBitrate", FormatBitrate(
            MediaBitrateResolver.ResolveOverallBitrate(mediaInfo, mkvInfo)));
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
            Add(rows, "MediaDetails_FieldBitrate", FormatBitrate(
                MediaBitrateResolver.ResolveTrackBitrate(stream?.Bitrate, mkvInfo, "video", index)));
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
            Add(rows, "MediaDetails_FieldBitrate", FormatBitrate(
                MediaBitrateResolver.ResolveTrackBitrate(stream?.Bitrate, mkvInfo, "audio", index)));
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

    private void RebuildSizes()
    {
        var mediaInfo = _source.DisplayInspection;
        var mkvInfo = _source.MkvInspection;
        var totalBytes = mediaInfo?.FileSizeBytes ?? mkvInfo?.FileSizeBytes;
        var duration = mediaInfo?.DurationNanoseconds ?? mkvInfo?.DurationNanoseconds;
        var entries = new List<MediaSizeEntry>();
        var trackEntries = new List<MediaSizeEntry>();
        var videoEntries = new List<MediaSizeEntry>();
        var audioEntries = new List<MediaSizeEntry>();
        var subtitleEntries = new List<MediaSizeEntry>();
        var attachmentEntries = new List<MediaSizeEntry>();

        var mediaVideo = mediaInfo?.VideoStreams ?? [];
        var mkvVideo = Tracks(mkvInfo, "video");
        var videoCount = Math.Max(mediaVideo.Count, mkvVideo.Count);
        for (var index = 0; index < videoCount; index++)
        {
            var stream = index < mediaVideo.Count ? mediaVideo[index] : null;
            var mkv = index < mkvVideo.Count ? mkvVideo[index] : null;
            var size = MediaSizeResolver.ResolveTrackSize(
                ReportedSize(_fullSizeReport?.VideoStreams, index) ?? stream?.StreamSizeBytes,
                mkvInfo,
                "video",
                index,
                MediaBitrateResolver.ResolveTrackBitrate(stream?.Bitrate, mkvInfo, "video", index),
                stream?.DurationNanoseconds ?? duration);
            videoEntries.Add(new MediaSizeEntry(
                AppText.Get("MediaDetails_Video"),
                AppText.Get("MediaDetails_TrackTitle", AppText.Get("MediaDetails_Video"), index + 1),
                BuildSizeDetails(stream?.Format ?? mkv?.CodecName, stream?.Language ?? mkv?.LanguageIetf ?? mkv?.Language, stream?.Title ?? mkv?.TrackName),
                size.Bytes,
                size.IsEstimated));
        }

        var mediaAudio = mediaInfo?.AudioStreams ?? [];
        var mkvAudio = Tracks(mkvInfo, "audio");
        var audioCount = Math.Max(mediaAudio.Count, mkvAudio.Count);
        for (var index = 0; index < audioCount; index++)
        {
            var stream = index < mediaAudio.Count ? mediaAudio[index] : null;
            var mkv = index < mkvAudio.Count ? mkvAudio[index] : null;
            var size = MediaSizeResolver.ResolveTrackSize(
                ReportedSize(_fullSizeReport?.AudioStreams, index) ?? stream?.StreamSizeBytes,
                mkvInfo,
                "audio",
                index,
                MediaBitrateResolver.ResolveTrackBitrate(stream?.Bitrate, mkvInfo, "audio", index),
                stream?.DurationNanoseconds ?? duration);
            audioEntries.Add(new MediaSizeEntry(
                AppText.Get("MediaDetails_Audio"),
                AppText.Get("MediaDetails_TrackTitle", AppText.Get("MediaDetails_Audio"), index + 1),
                BuildSizeDetails(stream?.Format ?? mkv?.CodecName, stream?.Language ?? mkv?.LanguageIetf ?? mkv?.Language, stream?.Title ?? mkv?.TrackName),
                size.Bytes,
                size.IsEstimated));
        }

        var mediaText = mediaInfo?.TextStreams ?? [];
        var mkvText = Tracks(mkvInfo, "subtitles");
        var textCount = Math.Max(mediaText.Count, mkvText.Count);
        for (var index = 0; index < textCount; index++)
        {
            var stream = index < mediaText.Count ? mediaText[index] : null;
            var mkv = index < mkvText.Count ? mkvText[index] : null;
            var size = MediaSizeResolver.ResolveTrackSize(
                ReportedSize(_fullSizeReport?.TextStreams, index) ?? stream?.StreamSizeBytes,
                mkvInfo,
                "subtitles",
                index,
                null,
                stream?.DurationNanoseconds ?? duration);
            subtitleEntries.Add(new MediaSizeEntry(
                AppText.Get("MediaDetails_Subtitle"),
                AppText.Get("MediaDetails_TrackTitle", AppText.Get("MediaDetails_Subtitle"), index + 1),
                BuildSizeDetails(stream?.Format ?? mkv?.CodecName, stream?.Language ?? mkv?.LanguageIetf ?? mkv?.Language, stream?.Title ?? mkv?.TrackName),
                size.Bytes,
                size.IsEstimated));
        }

        trackEntries.AddRange(videoEntries);
        trackEntries.AddRange(audioEntries);
        trackEntries.AddRange(subtitleEntries);
        entries.AddRange(trackEntries);
        foreach (var (attachment, index) in (mkvInfo?.Attachments ?? []).Select((value, index) => (value, index)))
        {
            attachmentEntries.Add(new MediaSizeEntry(
                AppText.Get("MediaSizes_AttachmentCategory"),
                attachment.FileName ?? AppText.Get("MediaDetails_AttachmentTitle", index + 1),
                BuildSizeDetails(attachment.ContentType, null, attachment.Description),
                attachment.Size,
                false));
        }
        entries.AddRange(attachmentEntries);

        _hasInexactTrackSizes = trackEntries.Any(static entry => entry.Bytes is null || entry.IsEstimated);
        var allContentKnown = entries.All(static entry => entry.Bytes is >= 0);
        var contentBytes = entries.Where(static entry => entry.Bytes is >= 0).Sum(static entry => entry.Bytes!.Value);
        if (totalBytes is >= 0 && allContentKnown)
        {
            var overheadBytes = Math.Max(0, totalBytes.Value - contentBytes);
            entries.Add(new MediaSizeEntry(
                AppText.Get("MediaSizes_OtherCategory"),
                AppText.Get("MediaSizes_ContainerOverhead"),
                AppText.Get("MediaSizes_ContainerOverheadDetails"),
                overheadBytes,
                entries.Any(static entry => entry.IsEstimated)));
            _containerOverheadText = FormatCompactFileSize(overheadBytes, entries.Any(static entry => entry.IsEstimated));
        }
        else
        {
            _containerOverheadText = AppText.Get("Common_Undetermined");
        }

        _sizeRows = entries.Select(entry => new MediaSizeDisplayRow(
                entry.Category,
                entry.Name,
                entry.Details,
                entry.Bytes is >= 0
                    ? FormatCompactFileSize(entry.Bytes.Value, entry.IsEstimated)
                    : AppText.Get("Common_Undetermined"),
                FormatShare(entry.Bytes, totalBytes),
                entry.Bytes is null
                    ? AppText.Get("MediaSizes_Unknown")
                    : AppText.Get(entry.IsEstimated ? "MediaSizes_Estimated" : "MediaSizes_Exact"),
                entry.Bytes is >= 0 ? $"{entry.Bytes.Value:N0} bytes" : null))
            .ToArray();

        _totalFileSizeText = totalBytes is >= 0
            ? FormatCompactFileSize(totalBytes.Value, false)
            : AppText.Get("Common_Undetermined");
        _videoSizeText = FormatAggregateSize(videoEntries);
        _audioSizeText = FormatAggregateSize(audioEntries);
        _subtitleAttachmentSizeText = FormatAggregateSize(subtitleEntries.Concat(attachmentEntries));

        var allTracksExact = trackEntries.Count > 0 && trackEntries.All(static entry => entry.Bytes is >= 0 && !entry.IsEstimated);
        _sizeStatusText = _fullSizeReport is not null
            ? allTracksExact
                ? AppText.Get("MediaSizes_ScanComplete")
                : AppText.Get("MediaSizes_ScanPartial")
            : allTracksExact
                ? AppText.Get("MediaSizes_StatisticsAvailable")
                : AppText.Get("MediaSizes_EstimatesAvailable");
        _canCalculateExactSizes = !_isCalculatingSizes && _hasInexactTrackSizes && File.Exists(Path);
        _copyText = BuildCopyText(_sections, _isProcessedBySubMux) + BuildSizeCopyText();

        RaiseSizeProperties();
    }

    private string BuildSizeCopyText()
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine($"[{AppText.Get("MediaSizes_TabTitle")}]");
        foreach (var row in _sizeRows)
        {
            builder.AppendLine($"{row.Name}: {row.Size} · {row.Share} · {row.Basis}");
        }
        builder.AppendLine($"{AppText.Get("MediaSizes_TotalFileSize")}: {_totalFileSizeText}");
        return builder.ToString().TrimEnd();
    }

    private static long? ReportedSize(IReadOnlyList<long?>? values, int index) =>
        values is not null && index >= 0 && index < values.Count ? values[index] : null;

    private static string BuildSizeDetails(params string?[] values) =>
        string.Join(" · ", values.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim()));

    private static string FormatAggregateSize(IEnumerable<MediaSizeEntry> source)
    {
        var entries = source.ToArray();
        if (entries.Length == 0)
        {
            return "0 B";
        }

        var known = entries.Where(static entry => entry.Bytes is >= 0).ToArray();
        if (known.Length == 0)
        {
            return AppText.Get("Common_Undetermined");
        }

        var bytes = known.Sum(static entry => entry.Bytes!.Value);
        var incomplete = known.Length != entries.Length;
        return $"{(incomplete ? "≥ " : string.Empty)}{FormatCompactFileSize(bytes, known.Any(static entry => entry.IsEstimated))}";
    }

    private static string FormatCompactFileSize(long bytes, bool estimated)
    {
        var prefix = estimated ? AppText.Get("MediaSizes_ApproximatePrefix") : string.Empty;
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{prefix}{bytes / 1024d / 1024d / 1024d:N2} GiB";
        }
        if (bytes >= 1024L * 1024L)
        {
            return $"{prefix}{bytes / 1024d / 1024d:N2} MiB";
        }
        if (bytes >= 1024L)
        {
            return $"{prefix}{bytes / 1024d:N2} KiB";
        }
        return $"{prefix}{bytes:N0} B";
    }

    private static string FormatShare(long? bytes, long? totalBytes)
    {
        if (bytes is not >= 0 || totalBytes is not > 0)
        {
            return "—";
        }

        var percentage = bytes.Value * 100d / totalBytes.Value;
        return percentage is > 0 and < 0.1 ? "<0.1%" : $"{percentage:0.0}%";
    }

    private void RaiseSizeProperties()
    {
        OnPropertyChanged(nameof(SizeRows));
        OnPropertyChanged(nameof(IsCalculatingSizes));
        OnPropertyChanged(nameof(CanCalculateExactSizes));
        OnPropertyChanged(nameof(SizeStatusText));
        OnPropertyChanged(nameof(TotalFileSizeText));
        OnPropertyChanged(nameof(VideoSizeText));
        OnPropertyChanged(nameof(AudioSizeText));
        OnPropertyChanged(nameof(SubtitleAttachmentSizeText));
        OnPropertyChanged(nameof(ContainerOverheadText));
        OnPropertyChanged(nameof(CopyText));
    }

    private sealed record MediaSizeEntry(
        string Category,
        string Name,
        string Details,
        long? Bytes,
        bool IsEstimated);

    private string BuildCopyText(
        IEnumerable<MediaInfoDetailSection> sections,
        bool isProcessedBySubMux)
    {
        var builder = new StringBuilder();
        builder.AppendLine(AppText.Get("MediaDetails_Title", _source.Name));
        builder.AppendLine($"{AppText.Get("MediaDetails_Path")}: {Path}");
        if (isProcessedBySubMux)
        {
            builder.AppendLine(AppText.Get("MediaDetails_ProcessedBySubMux"));
        }
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
