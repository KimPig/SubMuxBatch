using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Planning;

namespace SubMuxBatch.App.ViewModels;

public sealed class QueueItemViewModel : INotifyPropertyChanged
{
    private MediaSet _media;
    private ConversionPlan _plan;
    private JobState _state;
    private int _progress;
    private string? _error;
    private string? _outputPath;
    private string _plannedOutputFile = string.Empty;
    private MkvInspection? _mediaInspection;
    private string _mediaInfoStatus = "미디어 정보를 읽는 중…";

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
                return "중복";
            }

            if (_media.VideoPath is null)
            {
                return "—";
            }

            if (_mediaInspection is null)
            {
                return _mediaInfoStatus.StartsWith(
                    "미디어 정보 확인 실패",
                    StringComparison.Ordinal)
                        ? "확인 실패"
                        : "확인 중…";
            }

            var videoTracks = GetVideoTracks(_mediaInspection);
            if (videoTracks.Length == 0)
            {
                return "없음";
            }

            var primaryTrack = GetPrimaryVideoTrack(videoTracks)!;
            var suffix = videoTracks.Length > 1 ? $" +{videoTracks.Length - 1}" : string.Empty;
            return FormatCodec(primaryTrack) + suffix;
        }
    }
    public string PlanDescription => _plan.Description;
    public string OutputFile => _outputPath is null
        ? _plannedOutputFile
        : Path.GetFileName(_outputPath);
    public string VideoPathDisplay => _media.CandidateVideoPaths.Count == 0
        ? "없음"
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
            return text.Length > 0 ? text : "없음";
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
            ? "미정"
            : Path.Combine(Folder, _plannedOutputFile));
    public bool NeedsMediaInspection => _media.VideoPath is not null && _mediaInspection is null;
    public string InputSummary
    {
        get
        {
            if (_media.HasVideoConflict)
            {
                var names = string.Join(", ", _media.CandidateVideoPaths.Select(Path.GetFileName));
                return $"영상 중복 · {names}";
            }

            if (_media.VideoPath is null)
            {
                return "영상 없음";
            }

            if (_mediaInspection is null)
            {
                return _mediaInfoStatus;
            }

            var extensionLabel = Path.GetExtension(_media.VideoPath).TrimStart('.').ToUpperInvariant();
            var containerLabel = string.IsNullOrWhiteSpace(_mediaInspection.ContainerType)
                ? extensionLabel
                : _mediaInspection.ContainerType;
            var parts = new List<string>
            {
                string.IsNullOrWhiteSpace(extensionLabel)
                    ? containerLabel
                    : $"{containerLabel} ({extensionLabel})"
            };
            if (_mediaInspection.DurationNanoseconds is > 0)
            {
                parts.Add(FormatDuration(_mediaInspection.DurationNanoseconds.Value));
            }
            if (_mediaInspection.FileSizeBytes is >= 0)
            {
                parts.Add(FormatFileSize(_mediaInspection.FileSizeBytes.Value));
            }

            return string.Join(" · ", parts);
        }
    }
    public string VideoSummary
    {
        get
        {
            if (_mediaInspection is null)
            {
                return _media.VideoPath is null ? "—" : _mediaInfoStatus;
            }

            var track = GetPrimaryVideoTrack(GetVideoTracks(_mediaInspection));
            if (track is null)
            {
                return "비디오 트랙 없음";
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
            if (track.Bitrate is > 0)
            {
                parts.Add(FormatBitrate(track.Bitrate.Value));
            }

            return string.Join(" · ", parts);
        }
    }
    public string AudioSummary
    {
        get
        {
            if (_mediaInspection is null)
            {
                return _media.VideoPath is null ? "—" : _mediaInfoStatus;
            }

            var tracks = _mediaInspection.Tracks
                .Where(static track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (tracks.Length == 0)
            {
                return "오디오 트랙 없음";
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
                if (track.Bitrate is > 0)
                {
                    parts.Add(FormatBitrate(track.Bitrate.Value));
                }
                if (!string.IsNullOrWhiteSpace(track.TrackName))
                {
                    parts.Add(track.TrackName);
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
            if (_mediaInspection is null)
            {
                return _media.VideoPath is null ? "—" : _mediaInfoStatus;
            }

            var audioCount = _mediaInspection.Tracks.Count(static track =>
                string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase));
            var subtitleCount = _mediaInspection.Tracks.Count(static track =>
                string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase));
            var fontCount = _mediaInspection.Attachments.Count(MkvMergeClient.IsFontAttachment);
            var chapterCount = _mediaInspection.ChapterCount ?? 0;
            return $"오디오 {audioCount} · 자막 {subtitleCount} · 첨부 폰트 {fontCount} · 챕터 {chapterCount}";
        }
    }
    public string IssuesText
    {
        get
        {
            var issues = new List<string>();
            if (_plan.Error is not null) issues.Add(_plan.Error);
            issues.AddRange(_plan.Warnings);
            if (!string.IsNullOrWhiteSpace(Error)) issues.Add($"실행 오류: {Error}");
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
        JobState.Ready => _plan.Warnings.Count > 0 ? "준비 (경고)" : "준비",
        JobState.Invalid => "처리 불가",
        JobState.Queued => "대기 중",
        JobState.ConvertingSmiToSrt => "SMI → SRT",
        JobState.ConvertingAssToSrt => "ASS → SRT",
        JobState.ConvertingSrtToAss => "SRT → ASS",
        JobState.Muxing => $"MKV 병합 {Progress}%",
        JobState.Verifying => "검증 중",
        JobState.Succeeded => "완료",
        JobState.SucceededWithWarnings => "완료 (경고)",
        JobState.Skipped => "건너뜀",
        JobState.Failed => "실패",
        JobState.Cancelling => "취소 중",
        JobState.Cancelled => "취소됨",
        _ => State.ToString()
    };

    public string Details
    {
        get
        {
            var lines = new List<string>
            {
                $"기준 이름: {Name}",
                $"폴더: {Folder}",
                $"영상: {VideoPathDisplay}",
                $"ASS: {_media.AssPath ?? "없음"}",
                $"SRT: {_media.SrtPath ?? "없음"}",
                $"SMI: {_media.SmiPath ?? "없음"}",
                $"처리 계획: {_plan.Description}",
                $"출력: {OutputPath ?? OutputFile}"
            };

            if (_plan.Error is not null)
            {
                lines.Add($"오류: {_plan.Error}");
            }

            lines.AddRange(_plan.Warnings.Select(static warning => $"경고: {warning}"));
            if (!string.IsNullOrWhiteSpace(Error))
            {
                lines.Add($"실행 오류: {Error}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public void SetMediaInspection(MkvInspection inspection)
    {
        _mediaInspection = inspection;
        _mediaInfoStatus = string.Empty;
        RaiseMediaInfoChanged();
    }

    public void SetMediaInspectionError(string message)
    {
        _mediaInspection = null;
        _mediaInfoStatus = $"미디어 정보 확인 실패 · {message}";
        RaiseMediaInfoChanged();
    }

    public void Merge(MediaSet media, AppSettings settings)
    {
        var previousVideoPath = _media.VideoPath;
        _media = _media.Merge(media);
        if (!string.Equals(previousVideoPath, _media.VideoPath, StringComparison.OrdinalIgnoreCase))
        {
            _mediaInspection = null;
            _mediaInfoStatus = "미디어 정보를 읽는 중…";
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
        OnPropertyChanged(nameof(VideoCodecText));
        OnPropertyChanged(nameof(VideoSummary));
        OnPropertyChanged(nameof(AudioSummary));
        OnPropertyChanged(nameof(TrackSummary));
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

    private static string FormatLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || string.Equals(language, "und", StringComparison.OrdinalIgnoreCase))
        {
            return "언어 미지정";
        }

        var primary = language.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
        return primary.ToLowerInvariant() switch
        {
            "ko" or "kor" => "한국어",
            "ja" or "jpn" => "일본어",
            "en" or "eng" => "영어",
            _ => language
        };
    }

    private static string FormatDuration(long nanoseconds)
    {
        var duration = TimeSpan.FromTicks(nanoseconds / 100);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
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
