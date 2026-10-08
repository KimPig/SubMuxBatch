using System.Text;
using System.Text.Json;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Configuration;

public sealed record ProcessingPreset(
    string Id,
    string Name,
    ProcessingPresetSettings Settings);

public sealed class ProcessingPresetSettings
{
    public string OutputPrefix { get; set; } = OutputFileNaming.DefaultPrefix;
    public bool IncludeSubdirectories { get; set; }
    public bool AllowSubtitleSuffixMatch { get; set; }
    public bool RemoveExistingSubtitles { get; set; }
    public bool RemoveExistingFontAttachments { get; set; }
    public bool RemoveChapters { get; set; }
    public bool AttachAssStyleFonts { get; set; } = true;
    public bool AddSubMuxTag { get; set; } = true;
    public bool BackupOriginalMetadata { get; set; }
    public bool BackupOriginalSubtitles { get; set; }
    public bool BackupOriginalAttachments { get; set; }
    public bool BackupExcludedAudioTracks { get; set; }
    public bool CleanOutputMetadata { get; set; }
    public bool FilterAudioTracksByLanguage { get; set; }
    public AudioTrackLanguage SelectedAudioLanguage { get; set; } = AudioTrackLanguage.Japanese;
    public bool ConvertAudioToAac { get; set; }
    public AudioProcessingMode? AudioProcessingMode { get; set; }
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
    public bool UseCustomAssStyle { get; set; } = true;
    public int PlayResX { get; set; } = 1920;
    public int PlayResY { get; set; } = 1080;
    public string AssStyleLine { get; set; } = AppSettings.DefaultAssStyleLine;
    public bool MaintenanceUpdateAssStyle { get; set; } = true;
    public bool MaintenanceUpdateFonts { get; set; } = true;
    public bool MaintenanceApplyAudioSettings { get; set; } = true;
    public bool MaintenanceApplyVideoSettings { get; set; } = true;
    public bool MaintenanceRefreshTags { get; set; } = true;
    public bool MaintenanceDetectLegacyAss { get; set; } = true;
    public string MaintenanceLegacyAssStyles { get; set; } = AppSettings.DefaultMaintenanceLegacyAssStyles;
    public string MaintenanceOutputPrefix { get; set; } = AppSettings.GetDefaultMaintenanceOutputPrefix(AppLanguage.System);
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

    public void NormalizeLegacyValues()
    {
        AudioProcessingMode ??= ConvertAudioToAac
            ? SubMuxBatch.Core.Configuration.AudioProcessingMode.ConvertWhenNeeded
            : SubMuxBatch.Core.Configuration.AudioProcessingMode.KeepOriginal;
        ConvertAudioToAac = false;
    }

    public static ProcessingPresetSettings Capture(AppSettings value) => new()
    {
        OutputPrefix = value.OutputPrefix,
        IncludeSubdirectories = value.IncludeSubdirectories,
        AllowSubtitleSuffixMatch = value.AllowSubtitleSuffixMatch,
        RemoveExistingSubtitles = value.RemoveExistingSubtitles,
        RemoveExistingFontAttachments = value.RemoveExistingFontAttachments,
        RemoveChapters = value.RemoveChapters,
        AttachAssStyleFonts = value.AttachAssStyleFonts,
        AddSubMuxTag = value.AddSubMuxTag,
        BackupOriginalMetadata = value.BackupOriginalMetadata,
        BackupOriginalSubtitles = value.BackupOriginalSubtitles,
        BackupOriginalAttachments = value.BackupOriginalAttachments,
        BackupExcludedAudioTracks = value.BackupExcludedAudioTracks,
        CleanOutputMetadata = value.CleanOutputMetadata,
        FilterAudioTracksByLanguage = value.FilterAudioTracksByLanguage,
        SelectedAudioLanguage = value.SelectedAudioLanguage,
        AudioProcessingMode = value.AudioProcessingMode,
        AudioCodec = value.AudioCodec,
        AudioChannelMode = value.AudioChannelMode,
        AudioBitrateKbps = value.AudioBitrateKbps,
        VideoProcessingMode = value.VideoProcessingMode,
        VideoQualityProfile = value.VideoQualityProfile,
        CustomX265Preset = value.CustomX265Preset,
        CustomVideoRateControl = value.CustomVideoRateControl,
        CustomX265Crf = value.CustomX265Crf,
        CustomVideoBitrateKbps = value.CustomVideoBitrateKbps,
        CustomX265Tune = value.CustomX265Tune,
        VideoCpuUsage = value.VideoCpuUsage,
        CustomVideoThreadCount = value.CustomVideoThreadCount,
        CustomX265Parameters = value.CustomX265Parameters,
        UseCustomAssStyle = value.UseCustomAssStyle,
        PlayResX = value.PlayResX,
        PlayResY = value.PlayResY,
        AssStyleLine = value.AssStyleLine,
        MaintenanceUpdateAssStyle = value.MaintenanceUpdateAssStyle,
        MaintenanceUpdateFonts = value.MaintenanceUpdateFonts,
        MaintenanceApplyAudioSettings = value.MaintenanceApplyAudioSettings,
        MaintenanceApplyVideoSettings = value.MaintenanceApplyVideoSettings,
        MaintenanceRefreshTags = value.MaintenanceRefreshTags,
        MaintenanceDetectLegacyAss = value.MaintenanceDetectLegacyAss,
        MaintenanceLegacyAssStyles = value.MaintenanceLegacyAssStyles,
        MaintenanceOutputPrefix = value.MaintenanceOutputPrefix,
        MaintenanceReplaceOriginal = value.MaintenanceReplaceOriginal,
        EnableLapseSync = value.EnableLapseSync,
        EnableLapseValidationCheck = value.EnableLapseValidationCheck,
        LapseValidationWarningSeconds = value.LapseValidationWarningSeconds,
        LapseMode = value.LapseMode,
        LapseReference = value.LapseReference,
        LapseSplitPenalty = value.LapseSplitPenalty,
        LapseConfidenceThreshold = value.LapseConfidenceThreshold,
        WarnOnLargeLapseCorrection = value.WarnOnLargeLapseCorrection,
        LapseLargeCorrectionWarningSeconds = value.LapseLargeCorrectionWarningSeconds,
        MaintenanceUpdateLapseSync = value.MaintenanceUpdateLapseSync,
        MaintenanceForceLapseResync = value.MaintenanceForceLapseResync
    };

    public void ApplyTo(AppSettings value)
    {
        value.OutputPrefix = OutputPrefix;
        value.IncludeSubdirectories = IncludeSubdirectories;
        value.AllowSubtitleSuffixMatch = AllowSubtitleSuffixMatch;
        value.RemoveExistingSubtitles = RemoveExistingSubtitles;
        value.RemoveExistingFontAttachments = RemoveExistingFontAttachments;
        value.RemoveChapters = RemoveChapters;
        value.AttachAssStyleFonts = AttachAssStyleFonts;
        value.AddSubMuxTag = AddSubMuxTag;
        value.BackupOriginalMetadata = BackupOriginalMetadata;
        value.BackupOriginalSubtitles = BackupOriginalSubtitles;
        value.BackupOriginalAttachments = BackupOriginalAttachments;
        value.BackupExcludedAudioTracks = BackupExcludedAudioTracks;
        value.CleanOutputMetadata = CleanOutputMetadata;
        value.FilterAudioTracksByLanguage = FilterAudioTracksByLanguage;
        value.SelectedAudioLanguage = SelectedAudioLanguage;
        value.AudioProcessingMode = AudioProcessingMode
            ?? (ConvertAudioToAac
                ? SubMuxBatch.Core.Configuration.AudioProcessingMode.ConvertWhenNeeded
                : SubMuxBatch.Core.Configuration.AudioProcessingMode.KeepOriginal);
        value.ConvertAudioToAac = false;
        value.AudioCodec = AudioCodec;
        value.AudioChannelMode = AudioChannelMode;
        value.AudioBitrateKbps = AudioBitrateKbps;
        value.VideoProcessingMode = VideoProcessingMode;
        value.VideoQualityProfile = VideoQualityProfile;
        value.CustomX265Preset = CustomX265Preset;
        value.CustomVideoRateControl = CustomVideoRateControl;
        value.CustomX265Crf = CustomX265Crf;
        value.CustomVideoBitrateKbps = CustomVideoBitrateKbps;
        value.CustomX265Tune = CustomX265Tune;
        value.VideoCpuUsage = VideoCpuUsage;
        value.CustomVideoThreadCount = CustomVideoThreadCount;
        value.CustomX265Parameters = CustomX265Parameters;
        value.UseCustomAssStyle = UseCustomAssStyle;
        value.PlayResX = PlayResX;
        value.PlayResY = PlayResY;
        value.AssStyleLine = AssStyleLine;
        value.MaintenanceUpdateAssStyle = MaintenanceUpdateAssStyle;
        value.MaintenanceUpdateFonts = MaintenanceUpdateFonts;
        value.MaintenanceApplyAudioSettings = MaintenanceApplyAudioSettings;
        value.MaintenanceApplyVideoSettings = MaintenanceApplyVideoSettings;
        value.MaintenanceRefreshTags = MaintenanceRefreshTags;
        value.MaintenanceDetectLegacyAss = MaintenanceDetectLegacyAss;
        value.MaintenanceLegacyAssStyles = MaintenanceLegacyAssStyles;
        value.MaintenanceOutputPrefix = MaintenanceOutputPrefix;
        value.MaintenanceReplaceOriginal = MaintenanceReplaceOriginal;
        value.EnableLapseSync = EnableLapseSync;
        value.EnableLapseValidationCheck = EnableLapseValidationCheck;
        value.LapseValidationWarningSeconds = LapseValidationWarningSeconds;
        value.LapseMode = LapseMode;
        value.LapseReference = LapseReference;
        value.LapseSplitPenalty = LapseSplitPenalty;
        value.LapseConfidenceThreshold = LapseConfidenceThreshold;
        value.WarnOnLargeLapseCorrection = WarnOnLargeLapseCorrection;
        value.LapseLargeCorrectionWarningSeconds = LapseLargeCorrectionWarningSeconds;
        value.MaintenanceUpdateLapseSync = MaintenanceUpdateLapseSync;
        value.MaintenanceForceLapseResync = MaintenanceForceLapseResync;
    }
}

public sealed class ProcessingPresetStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly List<ProcessingPreset> _items;
    private readonly Dictionary<string, string> _paths;
    private readonly List<string> _loadWarnings;

    private ProcessingPresetStore(
        string directory,
        List<ProcessingPreset> items,
        Dictionary<string, string> paths,
        List<string> loadWarnings)
    {
        _directory = directory;
        _items = items;
        _paths = paths;
        _loadWarnings = loadWarnings;
    }

    public IReadOnlyList<ProcessingPreset> Items => _items;
    public IReadOnlyList<string> LoadWarnings => _loadWarnings;
    public static string DefaultDirectory => Path.Combine(AppSettings.SettingsDirectory, "presets");

    public static ProcessingPresetStore LoadOrCreate(
        AppSettings current,
        string? directory = null,
        string initialPresetName = "기본")
    {
        directory = Path.GetFullPath(directory ?? DefaultDirectory);
        Directory.CreateDirectory(directory);
        var items = new List<ProcessingPreset>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                var document = JsonSerializer.Deserialize<PresetDocument>(File.ReadAllText(path));
                if (document is null
                    || document.SchemaVersion != SchemaVersion
                    || string.IsNullOrWhiteSpace(document.Id)
                    || string.IsNullOrWhiteSpace(document.Name)
                    || document.Settings is null)
                {
                    throw new InvalidDataException("The preset document is invalid or uses an unsupported schema.");
                }

                ValidateFileName(document.Name);
                document.Settings.NormalizeLegacyValues();
                if (items.Any(item => string.Equals(item.Id, document.Id, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"Duplicate preset ID: {document.Id}");
                }
                if (items.Any(item => string.Equals(item.Name, document.Name, StringComparison.CurrentCultureIgnoreCase)))
                {
                    throw new InvalidDataException($"Duplicate preset name: {document.Name}");
                }

                var item = new ProcessingPreset(
                    document.Id,
                    document.Name.Trim(),
                    document.Settings);
                items.Add(item);
                paths[item.Id] = path;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or JsonException or InvalidDataException or ArgumentException)
            {
                warnings.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        var store = new ProcessingPresetStore(directory, items, paths, warnings);
        if (items.Count == 0)
        {
            var name = store.FindAvailableInitialName(
                string.IsNullOrWhiteSpace(initialPresetName) ? "기본" : initialPresetName);
            var initial = new ProcessingPreset(
                Guid.NewGuid().ToString("N"),
                name,
                ProcessingPresetSettings.Capture(current));
            store.WritePreset(initial);
            items.Add(initial);
            current.SelectedPresetId = initial.Id;
            return store;
        }

        store.SortItems();
        return store;
    }

    public ProcessingPreset Selected(AppSettings current) =>
        _items.FirstOrDefault(item => string.Equals(item.Id, current.SelectedPresetId, StringComparison.Ordinal))
        ?? _items[0];

    public ProcessingPreset Add(string name, AppSettings current)
    {
        var item = new ProcessingPreset(
            Guid.NewGuid().ToString("N"),
            NormalizeName(name, excludingId: null),
            ProcessingPresetSettings.Capture(current));
        WritePreset(item);
        _items.Add(item);
        SortItems();
        return item;
    }

    public ProcessingPreset AddDefaults(string requestedName = "기본")
    {
        var name = FindAvailableInitialName(
            string.IsNullOrWhiteSpace(requestedName) ? "기본" : requestedName);
        var item = new ProcessingPreset(
            Guid.NewGuid().ToString("N"),
            name,
            ProcessingPresetSettings.Capture(new AppSettings()));
        WritePreset(item);
        _items.Add(item);
        SortItems();
        return item;
    }

    public ProcessingPreset Duplicate(string id, string name)
    {
        var source = Required(id);
        var clone = JsonSerializer.Deserialize<ProcessingPresetSettings>(JsonSerializer.Serialize(source.Settings))!;
        var item = new ProcessingPreset(Guid.NewGuid().ToString("N"), NormalizeName(name, excludingId: null), clone);
        WritePreset(item);
        _items.Add(item);
        SortItems();
        return item;
    }

    public void Rename(string id, string name)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0) throw new KeyNotFoundException(id);
        var oldPath = _paths[id];
        var updated = _items[index] with { Name = NormalizeName(name, id) };
        WritePreset(updated, oldPath);
        _items[index] = updated;
        SortItems();
    }

    public void Update(string id, AppSettings current)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0) throw new KeyNotFoundException(id);
        var updated = _items[index] with { Settings = ProcessingPresetSettings.Capture(current) };
        WritePreset(updated, _paths[id]);
        _items[index] = updated;
    }

    public void Reset(string id)
    {
        var defaults = new AppSettings();
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0) throw new KeyNotFoundException(id);
        var updated = _items[index] with { Settings = ProcessingPresetSettings.Capture(defaults) };
        WritePreset(updated, _paths[id]);
        _items[index] = updated;
    }

    public void Delete(string id)
    {
        if (_items.Count <= 1) throw new InvalidOperationException(CoreText.Get("Preset_AtLeastOneRequired"));
        var item = Required(id);
        File.Delete(_paths[id]);
        _paths.Remove(id);
        _items.Remove(item);
    }

    private ProcessingPreset Required(string id) =>
        _items.FirstOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException(id);

    private string NormalizeName(string name, string? excludingId)
    {
        var normalized = name.Trim();
        if (normalized.Length == 0) throw new ArgumentException(CoreText.Get("Preset_NameRequired"), nameof(name));
        if (normalized.Length > 80) throw new ArgumentException(CoreText.Get("Preset_NameTooLong"), nameof(name));
        ValidateFileName(normalized);
        if (_items.Any(item => !string.Equals(item.Id, excludingId, StringComparison.Ordinal)
                               && string.Equals(item.Name, normalized, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException(CoreText.Get("Preset_NameDuplicate"));
        return normalized;
    }

    private void WritePreset(ProcessingPreset item, string? previousPath = null)
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, item.Name + ".json");
        var replacesPrevious = !string.IsNullOrWhiteSpace(previousPath)
                               && string.Equals(previousPath, destination, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(destination) && !replacesPrevious)
        {
            throw new IOException(CoreText.Get("Preset_FileDuplicate", Path.GetFileName(destination)));
        }

        var temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new PresetDocument(
                SchemaVersion,
                item.Id,
                item.Name,
                item.Settings);
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
            var verified = JsonSerializer.Deserialize<PresetDocument>(File.ReadAllText(temporary));
            if (verified is null
                || verified.SchemaVersion != SchemaVersion
                || !string.Equals(verified.Id, item.Id, StringComparison.Ordinal)
                || !string.Equals(verified.Name, item.Name, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The preset failed verification after it was written.");
            }

            File.Move(temporary, destination, overwrite: true);
            try
            {
                if (!string.IsNullOrWhiteSpace(previousPath)
                    && !replacesPrevious
                    && File.Exists(previousPath))
                {
                    File.Delete(previousPath);
                }
            }
            catch
            {
                if (!replacesPrevious && File.Exists(destination)) File.Delete(destination);
                throw;
            }
            _paths[item.Id] = destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void SortItems()
    {
        _items.Sort(static (left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
    }

    private string FindAvailableInitialName(string requestedName)
    {
        var baseName = requestedName.Trim();
        for (var suffix = 0; ; suffix++)
        {
            var candidate = suffix == 0 ? baseName : $"{baseName} ({suffix})";
            if (_items.Any(item => string.Equals(
                    item.Name,
                    candidate,
                    StringComparison.CurrentCultureIgnoreCase)))
            {
                continue;
            }
            var normalized = NormalizeName(candidate, excludingId: null);
            if (!File.Exists(Path.Combine(_directory, normalized + ".json")))
            {
                return normalized;
            }
        }
    }

    private static void ValidateFileName(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith(' ')
            || name.EndsWith('.'))
        {
            throw new ArgumentException(CoreText.Get("Preset_NameInvalidCharacters"), nameof(name));
        }

        var reserved = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (reserved is "CON" or "PRN" or "AUX" or "NUL"
            || (reserved.Length == 4
                && (reserved.StartsWith("COM", StringComparison.Ordinal)
                    || reserved.StartsWith("LPT", StringComparison.Ordinal))
                && reserved[3] is >= '1' and <= '9'))
        {
            throw new ArgumentException(CoreText.Get("Preset_NameReserved"), nameof(name));
        }
    }

    private sealed record PresetDocument(
        int SchemaVersion,
        string Id,
        string Name,
        ProcessingPresetSettings Settings);
}
