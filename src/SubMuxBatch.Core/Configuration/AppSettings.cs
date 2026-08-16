using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

[JsonConverter(typeof(JsonStringEnumConverter<AppLanguage>))]
public enum AppLanguage
{
    System,
    Korean,
    English
}

public sealed class AppSettings
{
    public const int MinConcurrentJobCount = 1;
    public const int MaxConcurrentJobCount = 8;
    public const double DefaultFileColumnWeight = 2.1;
    public const double DefaultCompositionColumnWeight = 0.75;
    public const double DefaultMediaFormatColumnWeight = 0.8;
    public const double DefaultDurationColumnWeight = 0.8;
    public const double DefaultVideoCodecColumnWeight = 1.1;
    public const double DefaultWorkColumnWeight = 1.9;
    public const double DefaultStatusColumnWeight = 1;

    public const string DefaultAssStyleLine =
        "Style: Default,\uB9D1\uC740 \uACE0\uB515,79.5,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,100,0.0,0,1,2.3,3.8,2,30,30,77,1";

    public string? MkvMergePath { get; set; }
    public string? SeConvPath { get; set; }
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public string OutputPrefix { get; set; } = OutputFileNaming.DefaultPrefix;
    public bool IncludeSubdirectories { get; set; }
    public bool AllowSubtitleSuffixMatch { get; set; }
    public bool RemoveExistingSubtitles { get; set; } = true;
    public bool RemoveExistingFontAttachments { get; set; } = false;
    public bool RemoveChapters { get; set; } = false;
    public bool AttachAssStyleFonts { get; set; } = true;
    public bool AddSubMuxTag { get; set; } = true;
    public bool FilterAudioTracksByLanguage { get; set; }
    public AudioTrackLanguage SelectedAudioLanguage { get; set; } = AudioTrackLanguage.Japanese;
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
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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
        return settings;
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
        SeConvPath = SeConvPath,
        Language = Language,
        OutputPrefix = OutputPrefix,
        IncludeSubdirectories = IncludeSubdirectories,
        AllowSubtitleSuffixMatch = AllowSubtitleSuffixMatch,
        RemoveExistingSubtitles = RemoveExistingSubtitles,
        RemoveExistingFontAttachments = RemoveExistingFontAttachments,
        RemoveChapters = RemoveChapters,
        AttachAssStyleFonts = AttachAssStyleFonts,
        AddSubMuxTag = AddSubMuxTag,
        FilterAudioTracksByLanguage = FilterAudioTracksByLanguage,
        SelectedAudioLanguage = SelectedAudioLanguage,
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
        AssStyleLine = AssStyleLine
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputPrefix)
            || OutputPrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidOutputPrefix"));
        }

        if (FilterAudioTracksByLanguage && !Enum.IsDefined(SelectedAudioLanguage))
        {
            throw new InvalidOperationException(CoreText.Get("Settings_InvalidAudioLanguage"));
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
}
