using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<AudioTrackLanguage>))]
public enum AudioTrackLanguage
{
    English,
    Japanese,
    Korean
}

[JsonConverter(typeof(JsonStringEnumConverter<AudioChannelMode>))]
public enum AudioChannelMode
{
    PreserveChannels,
    ConvertToStereo,
    KeepMultichannelAndAddStereo
}

[JsonConverter(typeof(JsonStringEnumConverter<AudioProcessingMode>))]
public enum AudioProcessingMode
{
    KeepOriginal,
    ConvertWhenNeeded,
    ReencodeAll
}

[JsonConverter(typeof(JsonStringEnumConverter<AudioCodec>))]
public enum AudioCodec
{
    AacLc,
    Opus
}

[JsonConverter(typeof(JsonStringEnumConverter<VideoProcessingMode>))]
public enum VideoProcessingMode
{
    KeepOriginal,
    ConvertNonHevc,
    ReencodeAll
}

[JsonConverter(typeof(JsonStringEnumConverter<VideoQualityProfile>))]
public enum VideoQualityProfile
{
    Fast,
    Balanced,
    HighQuality,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter<VideoRateControlMode>))]
public enum VideoRateControlMode
{
    ConstantQuality,
    AverageBitrate,
    TwoPassAverageBitrate
}

[JsonConverter(typeof(JsonStringEnumConverter<VideoCpuUsageMode>))]
public enum VideoCpuUsageMode
{
    Auto,
    Low,
    Normal,
    Maximum,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter<X265Preset>))]
public enum X265Preset
{
    Faster,
    Fast,
    Medium,
    Slow
}

[JsonConverter(typeof(JsonStringEnumConverter<X265Tune>))]
public enum X265Tune
{
    None,
    Animation
}

[JsonConverter(typeof(JsonStringEnumConverter<AppLanguage>))]
public enum AppLanguage
{
    System,
    Korean,
    English
}

[JsonConverter(typeof(JsonStringEnumConverter<LapseSyncMode>))]
public enum LapseSyncMode
{
    Auto,
    NoSplit,
    Ols,
    Split
}

[JsonConverter(typeof(JsonStringEnumConverter<LapseReferenceMode>))]
public enum LapseReferenceMode
{
    Auto,
    EmbeddedSubtitleOnly,
    AudioOnly
}

public sealed class AppSettings
{
    public const int MinConcurrentJobCount = 1;
    public const int MaxConcurrentJobCount = 8;
    public const int MinLapseConfidenceThreshold = 0;
    public const int MaxLapseConfidenceThreshold = 100;
    public const double MinLapseValidationWarningSeconds = 0.001;
    public const double MinLapseLargeCorrectionWarningSeconds = 0.1;
    public const double MaxLapseLargeCorrectionWarningSeconds = 3600;
    public const double DefaultFileColumnWeight = 2.1;
    public const double DefaultCompositionColumnWeight = 0.75;
    public const double DefaultMediaFormatColumnWeight = 0.8;
    public const double DefaultDurationColumnWeight = 0.8;
    public const double DefaultVideoCodecColumnWeight = 1.1;
    public const double DefaultWorkColumnWeight = 1.9;
    public const double DefaultStatusColumnWeight = 1;

    public const string DefaultAssStyleLine =
        "Style: Default,SubMux Sans,75,&H00FFFFFF,&HFF00FFFF,&H00000000,&H02000000,0,0,0,0,100,100,0,0,1,4,0,2,0,0,80,1";
    public const string LegacyMalgunGothicAssStyleLine =
        "Style: Default,맑은 고딕,75,&H00FFFFFF,&HFF00FFFF,&H00000000,&H02000000,-1,0,0,0,100,100,0,0,1,4,0,2,0,0,100,1";
    public static string DefaultMaintenanceLegacyAssStyles =>
        string.Join(Environment.NewLine, LegacyMalgunGothicAssStyleLine, DefaultAssStyleLine);

    public string? MkvMergePath { get; set; }
    public bool UseCustomMkvMergePath { get; set; }
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    public string OutputPrefix { get; set; } = OutputFileNaming.DefaultPrefix;
    public bool IncludeSubdirectories { get; set; }
    public bool AllowSubtitleSuffixMatch { get; set; }
    public bool RemoveExistingSubtitles { get; set; } = false;
    public bool RemoveExistingFontAttachments { get; set; } = false;
    public bool RemoveChapters { get; set; } = false;
    public bool AttachAssStyleFonts { get; set; } = true;
    public bool AddSubMuxTag { get; set; } = true;
    public bool BackupOriginalMetadata { get; set; } = false;
    public bool BackupOriginalSubtitles { get; set; } = false;
    public bool BackupOriginalAttachments { get; set; } = false;
    public bool BackupExcludedAudioTracks { get; set; } = false;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BackupOriginalSubtitlesAndAttachments { get; set; } = false;
    public bool CleanOutputMetadata { get; set; } = false;
    public bool FilterAudioTracksByLanguage { get; set; }
    public AudioTrackLanguage SelectedAudioLanguage { get; set; } = AudioTrackLanguage.Japanese;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ConvertAudioToAac { get; set; } = false;
    public AudioProcessingMode AudioProcessingMode { get; set; } = AudioProcessingMode.KeepOriginal;
    public AudioCodec AudioCodec { get; set; } = AudioCodec.AacLc;
    public AudioChannelMode AudioChannelMode { get; set; } = AudioChannelMode.PreserveChannels;
    public int AudioBitrateKbps { get; set; } = 192;
    public VideoProcessingMode VideoProcessingMode { get; set; } = VideoProcessingMode.KeepOriginal;
    public VideoQualityProfile VideoQualityProfile { get; set; } = VideoQualityProfile.Balanced;
    public X265Preset CustomX265Preset { get; set; } = X265Preset.Medium;
    public VideoRateControlMode CustomVideoRateControl { get; set; } = VideoRateControlMode.ConstantQuality;
    public int CustomX265Crf { get; set; } = 23;
    public int CustomVideoBitrateKbps { get; set; } = 3000;
    public X265Tune CustomX265Tune { get; set; } = X265Tune.None;
    public VideoCpuUsageMode VideoCpuUsage { get; set; } = VideoCpuUsageMode.Auto;
    public int CustomVideoThreadCount { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public string CustomX265Parameters { get; set; } = string.Empty;
    public int ConcurrentJobCount { get; set; } = MinConcurrentJobCount;
    public bool ShowFileColumn { get; set; } = true;
    public bool ShowCompositionColumn { get; set; } = true;
    public bool ShowMediaFormatColumn { get; set; } = true;
    public bool ShowDurationColumn { get; set; } = true;
    public bool ShowVideoCodecColumn { get; set; } = true;
    public bool ShowWorkColumn { get; set; } = true;
    public bool ShowStatusColumn { get; set; } = true;
    public double FileColumnWeight { get; set; } = DefaultFileColumnWeight;
    public double CompositionColumnWeight { get; set; } = DefaultCompositionColumnWeight;
    public double MediaFormatColumnWeight { get; set; } = DefaultMediaFormatColumnWeight;
    public double DurationColumnWeight { get; set; } = DefaultDurationColumnWeight;
    public double VideoCodecColumnWeight { get; set; } = DefaultVideoCodecColumnWeight;
    public double WorkColumnWeight { get; set; } = DefaultWorkColumnWeight;
    public double StatusColumnWeight { get; set; } = DefaultStatusColumnWeight;
    public bool ShowCompletionNotification { get; set; } = true;
    public bool PlayCompletionSound { get; set; }
    public bool UseCustomAssStyle { get; set; } = true;
    public int PlayResX { get; set; } = 1920;
    public int PlayResY { get; set; } = 1080;
    public string AssStyleLine { get; set; } = DefaultAssStyleLine;
    public bool MaintenanceUpdateAssStyle { get; set; } = true;
    public bool MaintenanceUpdateFonts { get; set; } = true;
    public bool MaintenanceApplyAudioSettings { get; set; } = true;
    public bool MaintenanceApplyVideoSettings { get; set; } = true;
    public bool MaintenanceRefreshTags { get; set; } = true;
    public bool MaintenanceDetectLegacyAss { get; set; } = true;
    public string MaintenanceLegacyAssStyles { get; set; } = DefaultMaintenanceLegacyAssStyles;
    public string MaintenanceOutputPrefix { get; set; } = GetDefaultMaintenanceOutputPrefix(AppLanguage.System);
    public bool MaintenanceReplaceOriginal { get; set; }
    public bool EnableLapseSync { get; set; }
    public bool EnableLapseValidationCheck { get; set; }
    public double LapseValidationWarningSeconds { get; set; } = 1;
    public LapseSyncMode LapseMode { get; set; } = LapseSyncMode.Auto;
    public LapseReferenceMode LapseReference { get; set; } = LapseReferenceMode.Auto;
    public int LapseSplitPenalty { get; set; } = 6;
    public int LapseConfidenceThreshold { get; set; } = 8;
    public bool WarnOnLargeLapseCorrection { get; set; } = true;
    public double LapseLargeCorrectionWarningSeconds { get; set; } = 1;
    public bool MaintenanceUpdateLapseSync { get; set; } = true;
    public bool MaintenanceForceLapseResync { get; set; }
    public string? SelectedPresetId { get; set; }

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SubMuxBatch");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    internal static string LegacySettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SubtitleBatch");

    internal static string LegacySettingsPath => Path.Combine(LegacySettingsDirectory, "settings.json");

    public static AppSettings Load() => LoadFromPaths(SettingsPath, LegacySettingsPath);

    internal static AppSettings LoadFromPaths(string settingsPath, string legacySettingsPath)
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                return Deserialize(File.ReadAllText(settingsPath));
            }

            if (!File.Exists(legacySettingsPath))
            {
                return new AppSettings();
            }

            var migrated = Deserialize(File.ReadAllText(legacySettingsPath));
            TrySaveToPath(migrated, settingsPath);
            return migrated;
        }
        catch
        {
            return new AppSettings();
        }
    }

    internal static AppSettings Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var hasMaintenanceOutputPrefix = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Any(property => string.Equals(
                property.Name,
                nameof(MaintenanceOutputPrefix),
                StringComparison.OrdinalIgnoreCase));
        var hasAudioProcessingMode = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Any(property => string.Equals(
                property.Name,
                nameof(AudioProcessingMode),
                StringComparison.OrdinalIgnoreCase));
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        if (!hasAudioProcessingMode)
        {
            settings.AudioProcessingMode = settings.ConvertAudioToAac
                ? AudioProcessingMode.ConvertWhenNeeded
                : AudioProcessingMode.KeepOriginal;
        }
        settings.ConvertAudioToAac = false;
        if (!settings.ShowFileColumn && !settings.ShowCompositionColumn && !settings.ShowMediaFormatColumn && !settings.ShowDurationColumn
            && !settings.ShowVideoCodecColumn && !settings.ShowWorkColumn && !settings.ShowStatusColumn)
        {
            settings.ShowFileColumn = true;
        }
        settings.FileColumnWeight = NormalizeQueueColumnWeight(settings.FileColumnWeight, DefaultFileColumnWeight);
        settings.CompositionColumnWeight = NormalizeQueueColumnWeight(settings.CompositionColumnWeight, DefaultCompositionColumnWeight);
        settings.MediaFormatColumnWeight = NormalizeQueueColumnWeight(settings.MediaFormatColumnWeight, DefaultMediaFormatColumnWeight);
        settings.DurationColumnWeight = NormalizeQueueColumnWeight(settings.DurationColumnWeight, DefaultDurationColumnWeight);
        settings.VideoCodecColumnWeight = NormalizeQueueColumnWeight(settings.VideoCodecColumnWeight, DefaultVideoCodecColumnWeight);
        settings.WorkColumnWeight = NormalizeQueueColumnWeight(settings.WorkColumnWeight, DefaultWorkColumnWeight);
        settings.StatusColumnWeight = NormalizeQueueColumnWeight(settings.StatusColumnWeight, DefaultStatusColumnWeight);
        if (!Enum.IsDefined(settings.Language))
        {
            settings.Language = AppLanguage.System;
        }
        if (settings.BackupOriginalSubtitlesAndAttachments)
        {
            settings.BackupOriginalSubtitles = true;
            settings.BackupOriginalAttachments = true;
            settings.BackupOriginalSubtitlesAndAttachments = false;
        }
        if (string.IsNullOrWhiteSpace(settings.MaintenanceLegacyAssStyles))
        {
            settings.MaintenanceLegacyAssStyles = DefaultMaintenanceLegacyAssStyles;
        }
        if (!hasMaintenanceOutputPrefix || string.IsNullOrWhiteSpace(settings.MaintenanceOutputPrefix))
        {
            settings.MaintenanceOutputPrefix = GetDefaultMaintenanceOutputPrefix(settings.Language);
        }
        return settings;
    }

    public static string GetDefaultMaintenanceOutputPrefix(AppLanguage language)
    {
        var korean = language == AppLanguage.Korean
                     || language == AppLanguage.System
                     && string.Equals(
                         CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                         "ko",
                         StringComparison.OrdinalIgnoreCase);
        return korean ? "유지보수_" : "Maintained_";
    }

    private static double NormalizeQueueColumnWeight(double weight, double defaultWeight) =>
        double.IsFinite(weight) && weight > 0 ? weight : defaultWeight;

    public void Save() => SaveToPath(this, SettingsPath);

    private static void SaveToPath(AppSettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new ArgumentException(CoreText.Get("Settings_PathNeedsDirectory"), nameof(path)));
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static void TrySaveToPath(AppSettings settings, string path)
    {
        try
        {
            SaveToPath(settings, path);
        }
        catch (IOException)
        {
            // The legacy settings are still returned in memory and can be saved later.
        }
        catch (UnauthorizedAccessException)
        {
            // The legacy settings are still returned in memory and can be saved later.
        }
    }

    public AppSettings Copy() => new()
    {
        MkvMergePath = MkvMergePath,
        UseCustomMkvMergePath = UseCustomMkvMergePath,
        Language = Language,
        CheckForUpdatesAutomatically = CheckForUpdatesAutomatically,
        OutputPrefix = OutputPrefix,
        IncludeSubdirectories = IncludeSubdirectories,
        AllowSubtitleSuffixMatch = AllowSubtitleSuffixMatch,
        RemoveExistingSubtitles = RemoveExistingSubtitles,
        RemoveExistingFontAttachments = RemoveExistingFontAttachments,
        RemoveChapters = RemoveChapters,
        AttachAssStyleFonts = AttachAssStyleFonts,
        AddSubMuxTag = AddSubMuxTag,
        BackupOriginalMetadata = BackupOriginalMetadata,
        BackupOriginalSubtitles = BackupOriginalSubtitles,
        BackupOriginalAttachments = BackupOriginalAttachments,
        BackupExcludedAudioTracks = BackupExcludedAudioTracks,
        CleanOutputMetadata = CleanOutputMetadata,
        FilterAudioTracksByLanguage = FilterAudioTracksByLanguage,
        SelectedAudioLanguage = SelectedAudioLanguage,
        ConvertAudioToAac = ConvertAudioToAac,
        AudioProcessingMode = AudioProcessingMode,
        AudioCodec = AudioCodec,
        AudioChannelMode = AudioChannelMode,
        AudioBitrateKbps = AudioBitrateKbps,
        VideoProcessingMode = VideoProcessingMode,
        VideoQualityProfile = VideoQualityProfile,
        CustomX265Preset = CustomX265Preset,
        CustomVideoRateControl = CustomVideoRateControl,
        CustomX265Crf = CustomX265Crf,
        CustomVideoBitrateKbps = CustomVideoBitrateKbps,
        CustomX265Tune = CustomX265Tune,
        VideoCpuUsage = VideoCpuUsage,
        CustomVideoThreadCount = CustomVideoThreadCount,
        CustomX265Parameters = CustomX265Parameters,
        ConcurrentJobCount = ConcurrentJobCount,
        ShowFileColumn = ShowFileColumn,
        ShowCompositionColumn = ShowCompositionColumn,
        ShowMediaFormatColumn = ShowMediaFormatColumn,
        ShowDurationColumn = ShowDurationColumn,
        ShowVideoCodecColumn = ShowVideoCodecColumn,
        ShowWorkColumn = ShowWorkColumn,
        ShowStatusColumn = ShowStatusColumn,
        FileColumnWeight = FileColumnWeight,
        CompositionColumnWeight = CompositionColumnWeight,
        MediaFormatColumnWeight = MediaFormatColumnWeight,
        DurationColumnWeight = DurationColumnWeight,
        VideoCodecColumnWeight = VideoCodecColumnWeight,
        WorkColumnWeight = WorkColumnWeight,
        StatusColumnWeight = StatusColumnWeight,
        ShowCompletionNotification = ShowCompletionNotification,
        PlayCompletionSound = PlayCompletionSound,
        UseCustomAssStyle = UseCustomAssStyle,
        PlayResX = PlayResX,
        PlayResY = PlayResY,
        AssStyleLine = AssStyleLine,
        MaintenanceUpdateAssStyle = MaintenanceUpdateAssStyle,
        MaintenanceUpdateFonts = MaintenanceUpdateFonts,
        MaintenanceApplyAudioSettings = MaintenanceApplyAudioSettings,
        MaintenanceApplyVideoSettings = MaintenanceApplyVideoSettings,
        MaintenanceRefreshTags = MaintenanceRefreshTags,
        MaintenanceDetectLegacyAss = MaintenanceDetectLegacyAss,
        MaintenanceLegacyAssStyles = MaintenanceLegacyAssStyles,
        MaintenanceOutputPrefix = MaintenanceOutputPrefix,
        MaintenanceReplaceOriginal = MaintenanceReplaceOriginal,
        EnableLapseSync = EnableLapseSync,
        EnableLapseValidationCheck = EnableLapseValidationCheck,
        LapseValidationWarningSeconds = LapseValidationWarningSeconds,
        LapseMode = LapseMode,
        LapseReference = LapseReference,
        LapseSplitPenalty = LapseSplitPenalty,
        LapseConfidenceThreshold = LapseConfidenceThreshold,
        WarnOnLargeLapseCorrection = WarnOnLargeLapseCorrection,
        LapseLargeCorrectionWarningSeconds = LapseLargeCorrectionWarningSeconds,
        MaintenanceUpdateLapseSync = MaintenanceUpdateLapseSync,
        MaintenanceForceLapseResync = MaintenanceForceLapseResync,
        SelectedPresetId = SelectedPresetId
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputPrefix)
            || OutputPrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidOutputPrefix"));
        }

        if (!MaintenanceReplaceOriginal
            && (string.IsNullOrWhiteSpace(MaintenanceOutputPrefix)
                || MaintenanceOutputPrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidMaintenanceOutputPrefix"));
        }

        if (FilterAudioTracksByLanguage && !Enum.IsDefined(SelectedAudioLanguage))
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidAudioLanguage"));
        }

        if (!Enum.IsDefined(AudioProcessingMode)
            || !Enum.IsDefined(AudioCodec)
            || !Enum.IsDefined(AudioChannelMode))
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidAudioChannelMode"));
        }

        if (AudioBitrateKbps is < 16 or > 1024
            || AudioCodec == AudioCodec.Opus && AudioBitrateKbps > 510)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidAudioBitrate"));
        }

        if (!Enum.IsDefined(VideoProcessingMode)
            || !Enum.IsDefined(VideoQualityProfile)
            || !Enum.IsDefined(CustomX265Preset)
            || !Enum.IsDefined(CustomVideoRateControl)
            || !Enum.IsDefined(VideoCpuUsage)
            || !Enum.IsDefined(CustomX265Tune))
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoEncoding"));
        }

        if (CustomX265Crf is < 0 or > 51)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoCrf"));
        }

        if (CustomVideoBitrateKbps is < 100 or > 200_000)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoBitrate"));
        }

        if (CustomVideoThreadCount is < 1 or > 256)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidVideoThreadCount"));
        }

        if (CustomX265Parameters.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidX265Parameters"));
        }

        var reservedX265Parameters = CustomX265Parameters
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static parameter => parameter.Split('=', 2)[0].Trim())
            .Where(static name => name.Equals("pass", StringComparison.OrdinalIgnoreCase)
                                  || name.Equals("stats", StringComparison.OrdinalIgnoreCase)
                                  || name.Equals("pools", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (reservedX265Parameters.Length > 0)
        {
            throw new InvalidOperationException(CoreText.Get(
                "Settings_ReservedX265Parameters",
                string.Join(", ", reservedX265Parameters.Distinct(StringComparer.OrdinalIgnoreCase))));
        }

        if (!Enum.IsDefined(LapseMode) || !Enum.IsDefined(LapseReference))
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidLapseMode"));
        }

        if (EnableLapseSync && EnableLapseValidationCheck)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidLapseOperationMode"));
        }

        if (EnableLapseValidationCheck
            && (!double.IsFinite(LapseValidationWarningSeconds)
                || LapseValidationWarningSeconds < MinLapseValidationWarningSeconds
                || LapseValidationWarningSeconds > MaxLapseLargeCorrectionWarningSeconds))
        {
            throw new InvalidOperationException(CoreText.Get(
                "Settings_InvalidLapseValidationWarning",
                MinLapseValidationWarningSeconds,
                MaxLapseLargeCorrectionWarningSeconds));
        }

        if (LapseSplitPenalty is < 1 or > 100)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidLapsePenalty"));
        }

        if (LapseConfidenceThreshold is < MinLapseConfidenceThreshold or > MaxLapseConfidenceThreshold)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidLapseConfidence"));
        }

        if (WarnOnLargeLapseCorrection
            && (!double.IsFinite(LapseLargeCorrectionWarningSeconds)
                || LapseLargeCorrectionWarningSeconds < MinLapseLargeCorrectionWarningSeconds
                || LapseLargeCorrectionWarningSeconds > MaxLapseLargeCorrectionWarningSeconds))
        {
            throw new InvalidOperationException(CoreText.Get(
                "Settings_InvalidLapseLargeCorrectionWarning",
                MinLapseLargeCorrectionWarningSeconds,
                MaxLapseLargeCorrectionWarningSeconds));
        }

        if (ConcurrentJobCount is < MinConcurrentJobCount or > MaxConcurrentJobCount)
        {
            throw new InvalidOperationException(CoreText.Get(
                "Settings_InvalidConcurrentJobs",
                MinConcurrentJobCount,
                MaxConcurrentJobCount));
        }

        if (PlayResX is < 16 or > 16384 || PlayResY is < 16 or > 16384)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidPlayRes"));
        }

        if (MaintenanceDetectLegacyAss)
        {
            var legacyStyles = MaintenanceLegacyAssStyles
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (legacyStyles.Length == 0
                || legacyStyles.Any(style => !AssStyleDefinition.TryParse(style, out _)))
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_InvalidLegacyStyles"));
            }
        }

        if (UseCustomAssStyle)
        {
            if (!AssStyleDefinition.TryParse(AssStyleLine, out var style, out var error))
            {
                throw new InvalidOperationException(error);
            }

            if (!string.Equals(style!.Name, "Default", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(CoreText.Get("Settings_AssStyleMustBeDefault"));
            }
        }
    }
}

public static class AssStyleTemplateWriter
{
    public static string Create(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var style = AssStyleDefinition.Parse(settings.AssStyleLine);

        return $"""
[Script Info]
ScriptType: v4.00+
PlayResX: {settings.PlayResX}
PlayResY: {settings.PlayResY}
WrapStyle: 0
ScaledBorderAndShadow: yes
YCbCr Matrix: TV.601

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
{style.ToStyleLine()}

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
""";
    }

    public static string CreateHeader(AppSettings settings)
    {
        var template = Create(settings);
        var formatIndex = template.LastIndexOf(
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
            StringComparison.Ordinal);
        return formatIndex < 0 ? template : template[..formatIndex].TrimEnd();
    }
}
