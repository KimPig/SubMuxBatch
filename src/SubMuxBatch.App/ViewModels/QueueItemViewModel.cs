using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using SubMuxBatch.App.Localization;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Media;
using SubMuxBatch.Core.Planning;

namespace SubMuxBatch.App.ViewModels;

public sealed class QueueItemViewModel : INotifyPropertyChanged
{
    public bool IsQueueEndSpacer => false;
    private MediaSet _media;
    private ConversionPlan _plan;
    private JobState _state;
    private int _progress;
    private string? _error;
    private string? _outputPath;
    private string _plannedOutputFile = string.Empty;
    private MkvInspection? _mediaInspection;
    private MediaInfoInspection? _displayInspection;
    private string _mediaInfoStatus = AppText.Get("MediaInfo_Loading");
    private bool _mediaInspectionFailed;
    private bool _mediaInspectionCompleted;

    public QueueItemViewModel(MediaSet media, AppSettings settings)
    {
        _media = media;
        _plan = ConversionPlanFactory.Create(media);
        _state = _plan.IsValid ? JobState.Ready : JobState.Invalid;
        RefreshPresentation(settings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaSet Media => _media;
    public ConversionPlan Plan => _plan;
    public MkvInspection? MkvInspection => _mediaInspection;
    public MediaInfoInspection? DisplayInspection => _displayInspection;
    public string Key => _media.Key.Canonical;
    public string Name => _media.Key.Stem;
    public string Folder => _media.Key.DirectoryPath;
    public string DetectedFiles
    {
        get
        {
            var files = _media.CandidateVideoPaths
                .Select(static path => Path.GetExtension(path).TrimStart('.').ToUpperInvariant())
                .Concat(new[]
                {
                    _media.AssPath is null ? null : "ASS",
                    _media.SrtPath is null ? null : "SRT",
                    _media.SmiPath is null ? null : "SMI"
                }.OfType<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return string.Join(" · ", files);
        }
    }
    public string MediaFormatText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_displayInspection?.ContainerFormat))
            {
                return FormatContainer(_displayInspection.ContainerFormat);
            }

            if (!string.IsNullOrWhiteSpace(_mediaInspection?.ContainerType))
            {
                return FormatContainer(_mediaInspection.ContainerType);
            }

            var formats = _media.CandidateVideoPaths
                .Select(static path => Path.GetExtension(path).TrimStart('.').ToUpperInvariant())
                .Where(static extension => extension.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return formats.Length == 0 ? "—" : string.Join(" / ", formats);
        }
    }
    public string VideoCodecText
    {
        get
        {
            if (_media.HasVideoConflict)
            {
                return AppText.Get("Common_Duplicate");
            }

            if (_media.VideoPath is null)
            {
                return "—";
            }

            if (_displayInspection?.VideoStreams.Count > 0)
            {
                var primaryStream = _displayInspection.VideoStreams[0];
                var displaySuffix = _displayInspection.VideoStreams.Count > 1
                    ? $" +{_displayInspection.VideoStreams.Count - 1}"
                    : string.Empty;
                return FormatCodec(primaryStream.Format, primaryStream.CodecId) + displaySuffix;
            }

            if (_mediaInspection is null)
            {
                return _mediaInspectionFailed
                    ? AppText.Get("Common_CheckFailed")
                    : AppText.Get("Common_Checking");
            }

            var videoTracks = GetVideoTracks(_mediaInspection);
            if (videoTracks.Length == 0)
            {
                return AppText.Get("Common_None");
            }

            var primaryTrack = GetPrimaryVideoTrack(videoTracks)!;
            var suffix = videoTracks.Length > 1 ? $" +{videoTracks.Length - 1}" : string.Empty;
            return FormatCodec(primaryTrack) + suffix;
        }
    }
    public string DurationText => GetDisplayDuration() is > 0 and var duration
        ? FormatDuration(duration)
        : _media.VideoPath is null ? "—" : !_mediaInspectionCompleted ? AppText.Get("Common_Checking") : "—";
    public string PlanDescription => _plan.Description;
    public string OutputFile => _outputPath is null
        ? _plannedOutputFile
        : Path.GetFileName(_outputPath);
    public string VideoPathDisplay => _media.CandidateVideoPaths.Count == 0
        ? AppText.Get("Common_None")
        : string.Join(Environment.NewLine, _media.CandidateVideoPaths);
    public string VideoFileNamesDisplay => _media.CandidateVideoPaths.Count == 0
        ? VideoPathDisplay
        : string.Join(" · ", _media.CandidateVideoPaths.Select(Path.GetFileName));    public string SubtitlePathsDisplay
    {
        get
        {
            var files = new[]
            {
                _media.AssPath is null ? null : $"ASS: {Path.GetFileName(_media.AssPath)}",
                _media.SrtPath is null ? null : $"SRT: {Path.GetFileName(_media.SrtPath)}",
                _media.SmiPath is null ? null : $"SMI: {Path.GetFileName(_media.SmiPath)}"
            }.Where(static value => value is not null);
            var text = string.Join(" · ", files);
            return text.Length > 0 ? text : AppText.Get("Common_None");
        }
    }
    public string SubtitlePathDisplay
    {
        get
        {
            var paths = new[] { _media.AssPath, _media.SrtPath, _media.SmiPath }
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            return paths.Length == 0 ? SubtitlePathsDisplay : string.Join(Environment.NewLine, paths);
        }
    }    public string OutputPathDisplay => OutputPath
        ?? (_media.VideoPath is null
            ? AppText.Get("Common_Undetermined")
            : Path.Combine(Folder, _plannedOutputFile));
    public bool NeedsMediaInspection => _media.VideoPath is not null && !_mediaInspectionCompleted;
    public string InputSummary
    {
        get
        {
            if (_media.HasVideoConflict)
            {
                var names = string.Join(", ", _media.CandidateVideoPaths.Select(Path.GetFileName));
                return AppText.Get("Queue_VideoConflict", names);
            }

            if (_media.VideoPath is null)
            {
                return AppText.Get("Queue_NoVideo");
            }

            if (!_mediaInspectionCompleted)
            {
                return _mediaInfoStatus;
            }
            if (_mediaInspectionFailed)
            {
                return _mediaInfoStatus;
            }

            var extensionLabel = Path.GetExtension(_media.VideoPath).TrimStart('.').ToUpperInvariant();
            var containerLabel = !string.IsNullOrWhiteSpace(_displayInspection?.ContainerFormat)
                ? FormatContainer(_displayInspection.ContainerFormat)
                : !string.IsNullOrWhiteSpace(_mediaInspection?.ContainerType)
                    ? FormatContainer(_mediaInspection.ContainerType)
                    : extensionLabel;
            var parts = new List<string>
            {
                string.IsNullOrWhiteSpace(extensionLabel)
                    ? containerLabel
                    : $"{containerLabel} ({extensionLabel})"
            };
            if (GetDisplayDuration() is > 0 and var duration)
            {
                parts.Add(FormatDuration(duration));
            }
            var fileSize = _displayInspection?.FileSizeBytes ?? _mediaInspection?.FileSizeBytes;
            if (fileSize is >= 0)
            {
                parts.Add(FormatFileSize(fileSize.Value));
            }
            return string.Join(" · ", parts);
        }
    }
    public string VideoSummary
    {
        get
        {
            if (_displayInspection?.VideoStreams.Count > 0)
            {
                var stream = _displayInspection.VideoStreams[0];
                var mediaParts = new List<string> { FormatCodec(stream.Format, stream.CodecId) };
                if (stream.Width is > 0 && stream.Height is > 0)
                {
                    mediaParts.Add($"{stream.Width}×{stream.Height}");
                }
                if (stream.FrameRate is > 0)
                {
                    mediaParts.Add($"{stream.FrameRate.Value:0.###} fps");
                }

                return string.Join(" · ", mediaParts);
            }

            if (_mediaInspection is null)
            {
                return _media.VideoPath is null ? "—" : _mediaInfoStatus;
            }

            var track = GetPrimaryVideoTrack(GetVideoTracks(_mediaInspection));
            if (track is null)
            {
                return AppText.Get("Queue_NoVideoTrack");
            }

            var parts = new List<string> { FormatCodec(track) };
            if (!string.IsNullOrWhiteSpace(track.PixelDimensions))
            {
                parts.Add(track.PixelDimensions.Replace("x", "×", StringComparison.OrdinalIgnoreCase));
            }
            if (track.DefaultDurationNanoseconds is > 0)
            {
                var fps = 1_000_000_000d / track.DefaultDurationNanoseconds.Value;
                parts.Add($"{fps:0.###} fps");
            }
            return string.Join(" · ", parts);
        }
    }
    public string AudioSummary
    {
        get
        {
            if (_displayInspection?.AudioStreams.Count > 0)
            {
                var streams = _displayInspection.AudioStreams;
                return string.Join(Environment.NewLine, streams.Select((stream, index) =>
                {
                    var parts = new List<string>
                    {
                        FormatCodec(stream.Format, stream.CodecId),
                        FormatLanguage(stream.Language)
                    };
                    if (stream.Channels is > 0)
                    {
                        parts.Add($"{stream.Channels}ch");
                    }
                    if (stream.SamplingRate is > 0)
                    {
                        parts.Add($"{stream.SamplingRate.Value / 1000d:0.#} kHz");
                    }
                    var prefix = streams.Count > 1 ? $"{index + 1}. " : string.Empty;
                    return prefix + string.Join(" · ", parts);
                }));
            }

            if (_mediaInspection is null)
            {
                return _media.VideoPath is null ? "—" : _mediaInfoStatus;
            }

            var tracks = _mediaInspection.Tracks
                .Where(static track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (tracks.Length == 0)
            {
                return AppText.Get("Queue_NoAudioTrack");
            }

            return string.Join(Environment.NewLine, tracks.Select((track, index) =>
            {
                var parts = new List<string>
                {
                    FormatCodec(track),
                    FormatLanguage(track.LanguageIetf ?? track.Language)
                };
                if (track.AudioChannels is > 0)
                {
                    parts.Add($"{track.AudioChannels}ch");
                }
                if (track.AudioSamplingFrequency is > 0)
                {
                    parts.Add($"{track.AudioSamplingFrequency.Value / 1000d:0.#} kHz");
                }
                var prefix = tracks.Length > 1 ? $"{index + 1}. " : string.Empty;
                return prefix + string.Join(" · ", parts);
            }));
        }
    }
    public string TrackSummary
    {
        get
        {
            if (!_mediaInspectionCompleted)
            {
                return _media.VideoPath is null ? "—" : _mediaInfoStatus;
            }
            if (_mediaInspectionFailed)
            {
                return _mediaInfoStatus;
            }

            var audioCount = _mediaInspection?.Tracks.Count(static track =>
                    string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase))
                ?? _displayInspection?.AudioStreams.Count
                ?? 0;
            var subtitleCount = _mediaInspection?.Tracks.Count(static track =>
                    string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase))
                ?? _displayInspection?.TextStreams.Count
                ?? 0;
            var fontCount = _mediaInspection?.Attachments.Count(MkvMergeClient.IsFontAttachment) ?? 0;
            var chapterCount = _mediaInspection?.ChapterCount ?? 0;
            return AppText.Get("Queue_TrackSummary", audioCount, subtitleCount, fontCount, chapterCount);
        }
    }
    public string MediaDetailsPath => _media.VideoPath ?? "—";
    public string MediaDetailsInputSummary
    {
        get
        {
            if (!_mediaInspectionCompleted || _mediaInspectionFailed)
            {
                return InputSummary;
            }

            var extension = _media.VideoPath is null
                ? string.Empty
                : Path.GetExtension(_media.VideoPath).TrimStart('.').ToUpperInvariant();
            var container = !string.IsNullOrWhiteSpace(_displayInspection?.ContainerFormat)
                ? FormatContainer(_displayInspection.ContainerFormat)
                : !string.IsNullOrWhiteSpace(_mediaInspection?.ContainerType)
                    ? FormatContainer(_mediaInspection.ContainerType)
                    : extension;
            var parts = new List<string>
            {
                string.IsNullOrWhiteSpace(extension) ? container : $"{container} ({extension})"
            };
            if (!string.IsNullOrWhiteSpace(_displayInspection?.ContainerProfile))
            {
                parts.Add(_displayInspection.ContainerProfile);
            }
            if (GetDisplayDuration() is > 0 and var duration)
            {
                parts.Add(FormatDetailedDuration(duration));
            }
            var fileSize = _displayInspection?.FileSizeBytes ?? _mediaInspection?.FileSizeBytes;
            if (fileSize is >= 0)
            {
                parts.Add(FormatFileSize(fileSize.Value));
            }
            if (_displayInspection?.OverallBitrate is > 0)
            {
                parts.Add(FormatBitrate(_displayInspection.OverallBitrate.Value));
            }

            return string.Join(" · ", parts);
        }
    }
    public string MediaDetailsVideoSummary
    {
        get
        {
            if (_displayInspection?.VideoStreams.Count is not > 0)
            {
                return VideoSummary;
            }

            var streams = _displayInspection.VideoStreams;
            return string.Join(Environment.NewLine, streams.Select((stream, index) =>
            {
                var parts = new List<string>
                {
                    FormatCodecWithProfile(stream.Format, stream.CodecId, stream.FormatProfile)
                };
                if (stream.Width is > 0 && stream.Height is > 0)
                {
                    parts.Add($"{stream.Width}×{stream.Height}");
                }
                if (stream.FrameRate is > 0)
                {
                    var mode = string.IsNullOrWhiteSpace(stream.FrameRateMode)
                        ? string.Empty
                        : $" {stream.FrameRateMode}";
                    parts.Add($"{stream.FrameRate.Value:0.###} fps{mode}");
                }
                if (stream.Bitrate is > 0)
                {
                    parts.Add(FormatBitrate(stream.Bitrate.Value));
                }
                if (stream.FrameCount is > 0)
                {
                    parts.Add(AppText.Get("MediaInfo_FrameCount", stream.FrameCount.Value));
                }
                if (stream.BitDepth is > 0)
                {
                    parts.Add($"{stream.BitDepth}-bit");
                }
                if (!string.IsNullOrWhiteSpace(stream.ScanType))
                {
                    parts.Add(stream.ScanType);
                }
                if (stream.DurationNanoseconds is > 0)
                {
                    parts.Add(FormatDetailedDuration(stream.DurationNanoseconds.Value));
                }

                var prefix = streams.Count > 1 ? $"{index + 1}. " : string.Empty;
                return prefix + string.Join(" · ", parts);
            }));
        }
    }
    public string MediaDetailsAudioSummary
    {
        get
        {
            if (_displayInspection?.AudioStreams.Count is not > 0)
            {
                return AudioSummary;
            }

            var streams = _displayInspection.AudioStreams;
            return string.Join(Environment.NewLine, streams.Select((stream, index) =>
            {
                var parts = new List<string>
                {
                    FormatCodecWithProfile(stream.Format, stream.CodecId, stream.FormatProfile),
                    FormatLanguage(stream.Language)
                };
                if (stream.Channels is > 0)
                {
                    parts.Add($"{stream.Channels}ch");
                }
                if (!string.IsNullOrWhiteSpace(stream.ChannelLayout))
                {
                    parts.Add(stream.ChannelLayout);
                }
                if (stream.SamplingRate is > 0)
                {
                    parts.Add($"{stream.SamplingRate.Value / 1000d:0.#} kHz");
                }
                if (stream.Bitrate is > 0)
                {
                    parts.Add(FormatBitrate(stream.Bitrate.Value));
                }
                if (stream.BitDepth is > 0)
                {
                    parts.Add($"{stream.BitDepth}-bit");
                }
                if (stream.DurationNanoseconds is > 0)
                {
                    parts.Add(FormatDetailedDuration(stream.DurationNanoseconds.Value));
                }
                if (!string.IsNullOrWhiteSpace(stream.Title))
                {
                    parts.Add(stream.Title);
                }

                var prefix = streams.Count > 1 ? $"{index + 1}. " : string.Empty;
                return prefix + string.Join(" · ", parts);
            }));
        }
    }
    public string MediaDetailsSubtitleSummary
    {
        get
        {
            if (_displayInspection?.TextStreams.Count > 0)
            {
                var streams = _displayInspection.TextStreams;
                return string.Join(Environment.NewLine, streams.Select((stream, index) =>
                {
                    var parts = new List<string>
                    {
                        FormatCodec(stream.Format, stream.CodecId),
                        FormatLanguage(stream.Language)
                    };
                    if (!string.IsNullOrWhiteSpace(stream.Title))
                    {
                        parts.Add(stream.Title);
                    }

                    var prefix = streams.Count > 1 ? $"{index + 1}. " : string.Empty;
                    return prefix + string.Join(" · ", parts);
                }));
            }

            var tracks = _mediaInspection?.Tracks
                .Where(static track => string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (tracks is not { Length: > 0 })
            {
                return AppText.Get("Common_None");
            }

            return string.Join(Environment.NewLine, tracks.Select((track, index) =>
            {
                var parts = new List<string>
                {
                    FormatCodec(track),
                    FormatLanguage(track.LanguageIetf ?? track.Language)
                };
                if (!string.IsNullOrWhiteSpace(track.TrackName))
                {
                    parts.Add(track.TrackName);
                }

                var prefix = tracks.Length > 1 ? $"{index + 1}. " : string.Empty;
                return prefix + string.Join(" · ", parts);
            }));
        }
    }
    public string MediaDetailsStructureSummary => TrackSummary;
    public string IssuesText
    {
        get
        {
            var issues = new List<string>();
            if (_plan.Error is not null) issues.Add(_plan.Error);
            issues.AddRange(_plan.Warnings);
            if (!string.IsNullOrWhiteSpace(Error)) issues.Add(AppText.Get("Queue_RuntimeError", Error));
            return string.Join(" · ", issues);
        }
    }
    public bool IsValid => _plan.IsValid;

    public JobState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusForeground));
                OnPropertyChanged(nameof(StatusBackground));
            }
        }
    }

    public int Progress
    {
        get => _progress;
        set
        {
            if (SetField(ref _progress, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(Details));
                OnPropertyChanged(nameof(IssuesText));
            }
        }
    }

    public string? OutputPath
    {
        get => _outputPath;
        set
        {
            if (SetField(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(OutputFile));
                OnPropertyChanged(nameof(Details));
                OnPropertyChanged(nameof(OutputPathDisplay));
            }
        }
    }

    public string StatusText => State switch
    {
        JobState.Ready => _plan.Warnings.Count > 0 ? AppText.Get("Status_ReadyWarning") : AppText.Get("Status_Ready"),
        JobState.Invalid => AppText.Get("Status_Invalid"),
        JobState.Queued => AppText.Get("Status_Queued"),
        JobState.ConvertingSmiToSrt => "SMI → SRT",
        JobState.ConvertingAssToSrt => "ASS → SRT",
        JobState.ConvertingSrtToAss => "SRT → ASS",
        JobState.Muxing => AppText.Get("Status_Muxing", Progress),
        JobState.Verifying => AppText.Get("Status_Verifying"),
        JobState.Succeeded => AppText.Get("Status_Succeeded"),
        JobState.SucceededWithWarnings => AppText.Get("Status_SucceededWarning"),
        JobState.Skipped => AppText.Get("Status_Skipped"),
        JobState.Failed => AppText.Get("Status_Failed"),
        JobState.Cancelling => AppText.Get("Status_Cancelling"),
        JobState.Cancelled => AppText.Get("Status_Cancelled"),
        _ => State.ToString()
    };

    public string StatusForeground => State switch
    {
        JobState.Succeeded => "#107C10",
        JobState.SucceededWithWarnings => "#9A6700",
        JobState.Ready when _plan.Warnings.Count > 0 => "#9A6700",
        JobState.Invalid or JobState.Skipped or JobState.Failed => "#C42B1C",
        JobState.Cancelling => "#A15C00",
        JobState.Cancelled => "#6B6B6B",
        _ => "#0067C0"
    };

    public string StatusBackground => State switch
    {
        JobState.Succeeded => "#E8F5E9",
        JobState.SucceededWithWarnings => "#FFF4CE",
        JobState.Ready when _plan.Warnings.Count > 0 => "#FFF4CE",
        JobState.Invalid or JobState.Skipped or JobState.Failed => "#FDE7E9",
        JobState.Cancelling => "#FFF4CE",
        _ => "Transparent"
    };

    public string Details
    {
        get
        {
            var lines = new List<string>
            {
                AppText.Get("Detail_BaseNameValue", Name),
                AppText.Get("Detail_FolderValue", Folder),
                AppText.Get("Detail_VideoValue", VideoPathDisplay),
                $"ASS: {_media.AssPath ?? AppText.Get("Common_None")}",
                $"SRT: {_media.SrtPath ?? AppText.Get("Common_None")}",
                $"SMI: {_media.SmiPath ?? AppText.Get("Common_None")}",
                AppText.Get("Detail_PlanValue", _plan.Description),
                AppText.Get("Detail_OutputValue", OutputPath ?? OutputFile)
            };

            if (_plan.Error is not null)
            {
                lines.Add(AppText.Get("Common_ErrorValue", _plan.Error));
            }

            lines.AddRange(_plan.Warnings.Select(static warning => AppText.Get("Common_WarningValue", warning)));
            if (!string.IsNullOrWhiteSpace(Error))
            {
                lines.Add(AppText.Get("Queue_RuntimeError", Error));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public void SetMediaInspection(MkvInspection inspection)
    {
        _mediaInspection = inspection;
        _displayInspection = null;
        _mediaInspectionFailed = false;
        _mediaInspectionCompleted = true;
        _mediaInfoStatus = string.Empty;
        RaiseMediaInfoChanged();
    }

    public void SetMediaInspections(MkvInspection? inspection, MediaInfoInspection? displayInspection)
    {
        _mediaInspection = inspection;
        _displayInspection = displayInspection;
        _mediaInspectionFailed = inspection is null && displayInspection is null;
        _mediaInspectionCompleted = true;
        _mediaInfoStatus = string.Empty;
        RaiseMediaInfoChanged();
    }

    public void SetMediaInspectionError(string message)
    {
        _mediaInspection = null;
        _displayInspection = null;
        _mediaInspectionFailed = true;
        _mediaInspectionCompleted = true;
        _mediaInfoStatus = AppText.Get("MediaInfo_Failed", message);
        RaiseMediaInfoChanged();
    }

    public void Merge(MediaSet media, AppSettings settings)
    {
        var previousVideoPath = _media.VideoPath;
        _media = _media.Merge(media);
        if (!string.Equals(previousVideoPath, _media.VideoPath, StringComparison.OrdinalIgnoreCase))
        {
            _mediaInspection = null;
            _displayInspection = null;
            _mediaInspectionFailed = false;
            _mediaInspectionCompleted = false;
            _mediaInfoStatus = AppText.Get("MediaInfo_Loading");
            RaiseMediaInfoChanged();
        }
        _plan = ConversionPlanFactory.Create(_media);
        if (State is JobState.Ready or JobState.Invalid or JobState.Failed or JobState.Skipped)
        {
            State = _plan.IsValid ? JobState.Ready : JobState.Invalid;
        }

        OnPropertyChanged(nameof(Media));
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(DetectedFiles));
        OnPropertyChanged(nameof(InputSummary));
        OnPropertyChanged(nameof(MediaFormatText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(VideoCodecText));
        OnPropertyChanged(nameof(VideoPathDisplay));
        OnPropertyChanged(nameof(VideoFileNamesDisplay));
        OnPropertyChanged(nameof(SubtitlePathsDisplay));
        OnPropertyChanged(nameof(SubtitlePathDisplay));
        OnPropertyChanged(nameof(PlanDescription));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(OutputPathDisplay));
        OnPropertyChanged(nameof(IssuesText));
        RefreshPresentation(settings);
    }

    public void RefreshPresentation(AppSettings settings)
    {
        _plannedOutputFile = _media.VideoPath is null
            ? "—"
            : OutputFileNaming.Create(_media.VideoPath, settings.OutputPrefix);
        OnPropertyChanged(nameof(OutputFile));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(OutputPathDisplay));
    }

    public void ApplyProgress(JobProgress progress)
    {
        State = progress.State;
        Progress = progress.Percent;
    }

    private void RaiseMediaInfoChanged()
    {
        OnPropertyChanged(nameof(NeedsMediaInspection));
        OnPropertyChanged(nameof(InputSummary));
        OnPropertyChanged(nameof(MediaFormatText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(VideoCodecText));
        OnPropertyChanged(nameof(VideoSummary));
        OnPropertyChanged(nameof(AudioSummary));
        OnPropertyChanged(nameof(TrackSummary));
        OnPropertyChanged(nameof(MediaDetailsPath));
        OnPropertyChanged(nameof(MediaDetailsInputSummary));
        OnPropertyChanged(nameof(MediaDetailsVideoSummary));
        OnPropertyChanged(nameof(MediaDetailsAudioSummary));
        OnPropertyChanged(nameof(MediaDetailsSubtitleSummary));
        OnPropertyChanged(nameof(MediaDetailsStructureSummary));
        OnPropertyChanged(nameof(MkvInspection));
        OnPropertyChanged(nameof(DisplayInspection));
    }

    private static MkvTrackInfo[] GetVideoTracks(MkvInspection inspection) =>
        inspection.Tracks
            .Where(static track => string.Equals(
                track.Type,
                "video",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static MkvTrackInfo? GetPrimaryVideoTrack(IReadOnlyList<MkvTrackInfo> videoTracks) =>
        videoTracks.FirstOrDefault(static track => track.DefaultTrack)
        ?? videoTracks.FirstOrDefault();

    private static string FormatCodec(MkvTrackInfo track)
    {
        var id = string.Join(' ', new[] { track.CodecId, track.CodecName }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (id.Contains("HEVC", StringComparison.OrdinalIgnoreCase)
            || id.Contains("H.265", StringComparison.OrdinalIgnoreCase)) return "HEVC/H.265";
        if (id.Contains("AVC", StringComparison.OrdinalIgnoreCase)
            || id.Contains("H.264", StringComparison.OrdinalIgnoreCase)) return "H.264/AVC";
        if (id.Contains("AV1", StringComparison.OrdinalIgnoreCase)) return "AV1";
        if (id.Contains("VP9", StringComparison.OrdinalIgnoreCase)) return "VP9";
        if (id.Contains("OPUS", StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (id.Contains("AAC", StringComparison.OrdinalIgnoreCase)) return "AAC";
        if (id.Contains("FLAC", StringComparison.OrdinalIgnoreCase)) return "FLAC";
        if (id.Contains("EAC3", StringComparison.OrdinalIgnoreCase)) return "E-AC-3";
        if (id.Contains("AC3", StringComparison.OrdinalIgnoreCase)) return "AC-3";
        if (id.Contains("DTS", StringComparison.OrdinalIgnoreCase)) return "DTS";
        return string.IsNullOrWhiteSpace(track.CodecName) ? track.CodecId : track.CodecName;
    }

    private static string FormatCodecWithProfile(string? format, string? codecId, string? profile)
    {
        var codec = FormatCodec(format, codecId);
        return string.IsNullOrWhiteSpace(profile)
            ? codec
            : $"{codec} {profile}";
    }

    private static string FormatCodec(string? format, string? codecId)
    {
        var id = string.Join(' ', new[] { format, codecId }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (id.Contains("HEVC", StringComparison.OrdinalIgnoreCase)
            || id.Contains("H.265", StringComparison.OrdinalIgnoreCase)) return "HEVC/H.265";
        if (id.Contains("AVC", StringComparison.OrdinalIgnoreCase)
            || id.Contains("H.264", StringComparison.OrdinalIgnoreCase)) return "H.264/AVC";
        if (id.Contains("AV1", StringComparison.OrdinalIgnoreCase)) return "AV1";
        if (id.Contains("VP9", StringComparison.OrdinalIgnoreCase)) return "VP9";
        if (id.Contains("OPUS", StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (id.Contains("AAC", StringComparison.OrdinalIgnoreCase)) return "AAC";
        if (id.Contains("FLAC", StringComparison.OrdinalIgnoreCase)) return "FLAC";
        if (id.Contains("E-AC-3", StringComparison.OrdinalIgnoreCase)
            || id.Contains("EAC3", StringComparison.OrdinalIgnoreCase)) return "E-AC-3";
        if (id.Contains("AC-3", StringComparison.OrdinalIgnoreCase)
            || id.Contains("AC3", StringComparison.OrdinalIgnoreCase)) return "AC-3";
        if (id.Contains("DTS", StringComparison.OrdinalIgnoreCase)) return "DTS";
        return string.IsNullOrWhiteSpace(format) ? codecId ?? "—" : format;
    }

    private static string FormatContainer(string container)
    {
        if (container.Contains("WebM", StringComparison.OrdinalIgnoreCase)) return "WebM";
        if (container.Contains("Matroska", StringComparison.OrdinalIgnoreCase)) return "MKV";
        if (container.Contains("MPEG-4", StringComparison.OrdinalIgnoreCase)
            || container.Contains("QuickTime", StringComparison.OrdinalIgnoreCase)) return "MP4";
        if (container.Contains("MPEG-TS", StringComparison.OrdinalIgnoreCase)
            || container.Contains("transport stream", StringComparison.OrdinalIgnoreCase)) return "MPEG-TS";
        if (container.Contains("AVI", StringComparison.OrdinalIgnoreCase)) return "AVI";
        return container;
    }

    private static string FormatLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || string.Equals(language, "und", StringComparison.OrdinalIgnoreCase))
        {
            return AppText.Get("Language_Undetermined");
        }

        var primary = language.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
        return primary.ToLowerInvariant() switch
        {
            "ko" or "kor" or "korean" => AppText.Get("Language_Korean"),
            "ja" or "jpn" or "japanese" => AppText.Get("Language_Japanese"),
            "en" or "eng" or "english" => AppText.Get("Language_English"),
            _ => language
        };
    }

    private long? GetDisplayDuration() =>
        _displayInspection?.DurationNanoseconds
        ?? _displayInspection?.VideoStreams.FirstOrDefault()?.DurationNanoseconds
        ?? _mediaInspection?.DurationNanoseconds;

    private static string FormatDuration(long nanoseconds)
    {
        var duration = TimeSpan.FromTicks(nanoseconds / 100);
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatDetailedDuration(long nanoseconds)
    {
        var duration = TimeSpan.FromTicks(nanoseconds / 100);
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";
    }

    private static string FormatFileSize(long bytes)
    {
        const double kib = 1024d;
        const double mib = kib * 1024d;
        const double gib = mib * 1024d;
        return bytes >= gib ? $"{bytes / gib:0.##} GiB"
            : bytes >= mib ? $"{bytes / mib:0.##} MiB"
            : bytes >= kib ? $"{bytes / kib:0.##} KiB"
            : $"{bytes} B";
    }

    private static string FormatBitrate(long bitrate) => bitrate >= 1_000_000
        ? $"{bitrate / 1_000_000d:0.##} Mbps"
        : $"{bitrate / 1000d:0.#} kbps";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
