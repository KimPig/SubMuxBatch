using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using SubMuxBatch.App.Localization;
using SubMuxBatch.App.Services;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Fonts;

namespace SubMuxBatch.App;

public partial class SettingsWindow : Window
{
    private static IReadOnlyList<AlignmentOption> CreateAlignmentOptions() =>
    [
        new(1, AppText.Get("Ass_AlignBottomLeft")),
        new(2, AppText.Get("Ass_AlignBottomCenter")),
        new(3, AppText.Get("Ass_AlignBottomRight")),
        new(4, AppText.Get("Ass_AlignMiddleLeft")),
        new(5, AppText.Get("Ass_AlignCenter")),
        new(6, AppText.Get("Ass_AlignMiddleRight")),
        new(7, AppText.Get("Ass_AlignTopLeft")),
        new(8, AppText.Get("Ass_AlignTopCenter")),
        new(9, AppText.Get("Ass_AlignTopRight"))
    ];

    private readonly IReadOnlyList<AlignmentOption> _alignmentOptions = CreateAlignmentOptions();

    private int _playResX;
    private int _playResY;
    private string _assStyleLine = AppSettings.DefaultAssStyleLine;
    private AssStyleDefinition _styleDefinition = AssStyleDefinition.Parse(AppSettings.DefaultAssStyleLine);
    private bool _fontStatusRefreshInProgress;

    public SettingsWindow(
        AppSettings settings,
        BundledToolStatus? bundledToolStatus = null,
        bool openMaintenanceTab = false,
        bool openToolsTab = false)
    {
        InitializeComponent();
        if (openMaintenanceTab)
        {
            SettingsTabControl.SelectedItem = MaintenanceTabItem;
        }
        else if (openToolsTab)
        {
            SettingsTabControl.SelectedItem = OtherTabItem;
        }
        Settings = settings;
        AlignmentComboBox.ItemsSource = _alignmentOptions;
        var version = typeof(SettingsWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        BuiltInVersionText.Text = AppText.Get("Settings_VersionSummary", version);
        LoadSettingsIntoControls(settings);
        BundledToolsStatusText.Text = bundledToolStatus?.IsHealthy == false
            ? AppText.Get("Settings_BundledToolsFailed", bundledToolStatus.Error ?? "Unknown error")
            : AppText.Get("Settings_BundledToolsReady");
        SetBundledToolsStatusAppearance(bundledToolStatus?.IsHealthy != false);

        Loaded += async (_, _) =>
        {
            WindowPlacementHelper.FitToCurrentWorkingArea(this);
            await RefreshSubMuxSansStatusAsync();
        };
        Activated += async (_, _) => await RefreshSubMuxSansStatusAsync();
    }

    public AppSettings Settings { get; private set; }
    public Func<Window, Task>? UpdateCheckRequested { get; set; }

    private void LoadSettingsIntoControls(AppSettings settings)
    {
        LanguageComboBox.SelectedValue = settings.Language.ToString();
        if (LanguageComboBox.SelectedIndex < 0)
        {
            LanguageComboBox.SelectedValue = AppLanguage.System.ToString();
        }
        CheckForUpdatesAutomaticallyCheckBox.IsChecked = settings.CheckForUpdatesAutomatically;
        OutputPrefixTextBox.Text = settings.OutputPrefix;
        IncludeSubdirectoriesCheckBox.IsChecked = settings.IncludeSubdirectories;
        AllowSubtitleSuffixMatchCheckBox.IsChecked = settings.AllowSubtitleSuffixMatch;
        RemoveExistingSubtitlesCheckBox.IsChecked = settings.RemoveExistingSubtitles;
        RemoveExistingFontAttachmentsCheckBox.IsChecked = settings.RemoveExistingFontAttachments;
        RemoveChaptersCheckBox.IsChecked = settings.RemoveChapters;
        AttachAssStyleFontsCheckBox.IsChecked = settings.AttachAssStyleFonts;
        AddSubMuxTagCheckBox.IsChecked = settings.AddSubMuxTag;
        BackupOriginalMetadataCheckBox.IsChecked = settings.BackupOriginalMetadata;
        BackupOriginalSubtitlesCheckBox.IsChecked = settings.BackupOriginalSubtitles;
        BackupOriginalAttachmentsCheckBox.IsChecked = settings.BackupOriginalAttachments;
        BackupExcludedAudioTracksCheckBox.IsChecked = settings.BackupExcludedAudioTracks;
        CleanOutputMetadataCheckBox.IsChecked = settings.CleanOutputMetadata;
        FilterAudioTracksByLanguageCheckBox.IsChecked = settings.FilterAudioTracksByLanguage;
        AudioLanguageComboBox.SelectedValue = settings.SelectedAudioLanguage.ToString();
        if (AudioLanguageComboBox.SelectedIndex < 0)
        {
            AudioLanguageComboBox.SelectedValue = AudioTrackLanguage.Japanese.ToString();
        }
        AudioProcessingModeComboBox.SelectedValue = settings.AudioProcessingMode.ToString();
        AudioCodecComboBox.SelectedValue = settings.AudioCodec.ToString();
        AudioChannelModeComboBox.SelectedValue = settings.AudioChannelMode.ToString();
        if (AudioChannelModeComboBox.SelectedIndex < 0)
        {
            AudioChannelModeComboBox.SelectedValue = AudioChannelMode.PreserveChannels.ToString();
        }
        AudioBitrateComboBox.Text = settings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture);
        VideoProcessingModeComboBox.SelectedValue = settings.VideoProcessingMode.ToString();
        VideoQualityProfileComboBox.SelectedValue = settings.VideoQualityProfile.ToString();
        CustomX265PresetComboBox.SelectedValue = settings.CustomX265Preset.ToString();
        CustomVideoRateControlComboBox.SelectedValue = settings.CustomVideoRateControl.ToString();
        CustomX265CrfTextBox.Text = settings.CustomX265Crf.ToString(CultureInfo.InvariantCulture);
        CustomVideoBitrateComboBox.Text = settings.CustomVideoBitrateKbps.ToString(CultureInfo.InvariantCulture);
        CustomX265TuneComboBox.SelectedValue = settings.CustomX265Tune.ToString();
        VideoCpuUsageComboBox.SelectedValue = settings.VideoCpuUsage.ToString();
        CustomVideoThreadCountTextBox.Text = settings.CustomVideoThreadCount.ToString(CultureInfo.InvariantCulture);
        CustomX265ParametersTextBox.Text = settings.CustomX265Parameters;
        UpdateAudioEncodingControls();
        UpdateVideoEncodingControls();
        ConcurrentJobCountComboBox.SelectedValue = settings.ConcurrentJobCount.ToString();
        if (ConcurrentJobCountComboBox.SelectedIndex < 0)
        {
            ConcurrentJobCountComboBox.SelectedValue = AppSettings.MinConcurrentJobCount.ToString();
        }
        ShowCompletionNotificationCheckBox.IsChecked = settings.ShowCompletionNotification;
        PlayCompletionSoundCheckBox.IsChecked = settings.PlayCompletionSound;
        UseCustomAssStyleCheckBox.IsChecked = settings.UseCustomAssStyle;
        MaintenanceUpdateAssStyleCheckBox.IsChecked = settings.MaintenanceUpdateAssStyle;
        MaintenanceUpdateFontsCheckBox.IsChecked = settings.MaintenanceUpdateFonts;
        MaintenanceApplyAudioSettingsCheckBox.IsChecked = settings.MaintenanceApplyAudioSettings;
        MaintenanceApplyVideoSettingsCheckBox.IsChecked = settings.MaintenanceApplyVideoSettings;
        MaintenanceRefreshTagsCheckBox.IsChecked = settings.MaintenanceRefreshTags;
        MaintenanceDetectLegacyAssCheckBox.IsChecked = settings.MaintenanceDetectLegacyAss;
        MaintenanceLegacyAssStylesTextBox.Text = settings.MaintenanceLegacyAssStyles;
        MaintenanceOutputPrefixTextBox.Text = settings.MaintenanceOutputPrefix;
        MaintenanceReplaceOriginalCheckBox.IsChecked = settings.MaintenanceReplaceOriginal;
        LapseAutoSyncRadioButton.IsChecked = settings.EnableLapseSync;
        LapseValidationRadioButton.IsChecked = !settings.EnableLapseSync && settings.EnableLapseValidationCheck;
        LapseDisabledRadioButton.IsChecked = !settings.EnableLapseSync && !settings.EnableLapseValidationCheck;
        LapseValidationWarningSecondsTextBox.Text =
            settings.LapseValidationWarningSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        LapseModeComboBox.SelectedValue = settings.LapseMode.ToString();
        UseEmbeddedSubtitleReferenceCheckBox.IsChecked =
            settings.LapseReference != LapseReferenceMode.AudioOnly;
        LapseSplitPenaltyTextBox.Text = settings.LapseSplitPenalty.ToString(CultureInfo.InvariantCulture);
        LapseConfidenceTextBox.Text = settings.LapseConfidenceThreshold.ToString(CultureInfo.InvariantCulture);
        WarnOnLargeLapseCorrectionCheckBox.IsChecked = settings.WarnOnLargeLapseCorrection;
        LapseLargeCorrectionWarningSecondsTextBox.Text =
            settings.LapseLargeCorrectionWarningSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        MaintenanceUpdateLapseSyncCheckBox.IsChecked = settings.MaintenanceUpdateLapseSync;
        MaintenanceForceLapseResyncCheckBox.IsChecked = settings.MaintenanceForceLapseResync;
        UpdateLapseOperationControls();
        UpdateLapseSplitPenaltyControls();
        UpdateMaintenanceOutputControls();
        _playResX = settings.PlayResX;
        _playResY = settings.PlayResY;
        _styleDefinition = ParseStyleOrDefault(settings.AssStyleLine);
        _assStyleLine = _styleDefinition.ToStyleLine();
        PopulateAssStyleFields();
    }

    private async void CheckForUpdatesNow_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateCheckRequested is null)
        {
            return;
        }

        CheckForUpdatesNowButton.IsEnabled = false;
        var originalContent = CheckForUpdatesNowButton.Content;
        CheckForUpdatesNowButton.Content = AppText.Get("Settings_CheckingForUpdates");
        try
        {
            await UpdateCheckRequested(this);
        }
        finally
        {
            CheckForUpdatesNowButton.Content = originalContent;
            CheckForUpdatesNowButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var updated = Settings.Copy();
            if (LanguageComboBox.SelectedValue is not string selectedLanguage
                || !Enum.TryParse(selectedLanguage, out AppLanguage language)
                || !Enum.IsDefined(language))
            {
                throw new InvalidOperationException(AppText.Get("Settings_SelectLanguageError"));
            }
            updated.Language = language;
            updated.CheckForUpdatesAutomatically = CheckForUpdatesAutomaticallyCheckBox.IsChecked == true;
            updated.MkvMergePath = null;
            updated.UseCustomMkvMergePath = false;
            updated.OutputPrefix = OutputPrefixTextBox.Text.Trim();
            updated.IncludeSubdirectories = IncludeSubdirectoriesCheckBox.IsChecked == true;
            updated.AllowSubtitleSuffixMatch = AllowSubtitleSuffixMatchCheckBox.IsChecked == true;
            updated.RemoveExistingSubtitles = RemoveExistingSubtitlesCheckBox.IsChecked == true;
            updated.RemoveExistingFontAttachments = RemoveExistingFontAttachmentsCheckBox.IsChecked == true;
            updated.RemoveChapters = RemoveChaptersCheckBox.IsChecked == true;
            updated.AttachAssStyleFonts = AttachAssStyleFontsCheckBox.IsChecked == true;
            updated.AddSubMuxTag = AddSubMuxTagCheckBox.IsChecked == true;
            updated.BackupOriginalMetadata = BackupOriginalMetadataCheckBox.IsChecked == true;
            updated.BackupOriginalSubtitles = BackupOriginalSubtitlesCheckBox.IsChecked == true;
            updated.BackupOriginalAttachments = BackupOriginalAttachmentsCheckBox.IsChecked == true;
            updated.BackupExcludedAudioTracks = BackupExcludedAudioTracksCheckBox.IsChecked == true;
            updated.CleanOutputMetadata = CleanOutputMetadataCheckBox.IsChecked == true;
            updated.FilterAudioTracksByLanguage = FilterAudioTracksByLanguageCheckBox.IsChecked == true;
            if (AudioLanguageComboBox.SelectedValue is not string selectedAudioLanguage
                || !Enum.TryParse(selectedAudioLanguage, out AudioTrackLanguage audioLanguage)
                || !Enum.IsDefined(audioLanguage))
            {
                throw new InvalidOperationException(AppText.Get("Settings_SelectAudioLanguageError"));
            }
            updated.SelectedAudioLanguage = audioLanguage;
            updated.AudioProcessingMode = ParseSelectedEnum<AudioProcessingMode>(
                AudioProcessingModeComboBox, "Settings_SelectAudioProcessingModeError");
            updated.AudioCodec = ParseSelectedEnum<AudioCodec>(AudioCodecComboBox, "Settings_SelectAudioCodecError");
            if (AudioChannelModeComboBox.SelectedValue is not string selectedAudioChannelMode
                || !Enum.TryParse(selectedAudioChannelMode, out AudioChannelMode audioChannelMode)
                || !Enum.IsDefined(audioChannelMode))
            {
                throw new InvalidOperationException(AppText.Get("Settings_SelectAudioChannelModeError"));
            }
            updated.AudioChannelMode = audioChannelMode;
            if (!int.TryParse(AudioBitrateComboBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var audioBitrateKbps))
            {
                throw new InvalidOperationException(AppText.Get("Settings_InvalidAudioBitrate"));
            }
            updated.AudioBitrateKbps = audioBitrateKbps;
            updated.VideoProcessingMode = ParseSelectedEnum<VideoProcessingMode>(
                VideoProcessingModeComboBox, "Settings_SelectVideoProcessingModeError");
            updated.VideoQualityProfile = ParseSelectedEnum<VideoQualityProfile>(
                VideoQualityProfileComboBox, "Settings_SelectVideoQualityError");
            updated.CustomX265Preset = ParseSelectedEnum<X265Preset>(
                CustomX265PresetComboBox, "Settings_SelectX265PresetError");
            updated.CustomVideoRateControl = ParseSelectedEnum<VideoRateControlMode>(
                CustomVideoRateControlComboBox, "Settings_SelectVideoRateControlError");
            if (!int.TryParse(CustomX265CrfTextBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var customX265Crf))
            {
                throw new InvalidOperationException(AppText.Get("Settings_InvalidVideoCrf"));
            }
            updated.CustomX265Crf = customX265Crf;
            if (!int.TryParse(CustomVideoBitrateComboBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var customVideoBitrateKbps))
            {
                throw new InvalidOperationException(AppText.Get("Settings_InvalidVideoBitrate"));
            }
            updated.CustomVideoBitrateKbps = customVideoBitrateKbps;
            updated.CustomX265Tune = ParseSelectedEnum<X265Tune>(
                CustomX265TuneComboBox, "Settings_SelectX265TuneError");
            updated.VideoCpuUsage = ParseSelectedEnum<VideoCpuUsageMode>(
                VideoCpuUsageComboBox, "Settings_SelectVideoCpuUsageError");
            if (!int.TryParse(CustomVideoThreadCountTextBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var customVideoThreadCount))
            {
                throw new InvalidOperationException(AppText.Get("Settings_InvalidVideoThreadCount"));
            }
            updated.CustomVideoThreadCount = customVideoThreadCount;
            updated.CustomX265Parameters = CustomX265ParametersTextBox.Text.Trim();
            if (ConcurrentJobCountComboBox.SelectedValue is not string concurrentJobCountText
                || !int.TryParse(concurrentJobCountText, out var concurrentJobCount))
            {
                throw new InvalidOperationException(AppText.Get("Settings_SelectConcurrentJobsError"));
            }
            updated.ConcurrentJobCount = concurrentJobCount;
            updated.ShowCompletionNotification = ShowCompletionNotificationCheckBox.IsChecked == true;
            updated.PlayCompletionSound = PlayCompletionSoundCheckBox.IsChecked == true;
            updated.UseCustomAssStyle = UseCustomAssStyleCheckBox.IsChecked == true;
            if (updated.UseCustomAssStyle)
            {
                CommitAssStyleFields();
            }
            updated.PlayResX = _playResX;
            updated.PlayResY = _playResY;
            updated.AssStyleLine = _assStyleLine;
            updated.MaintenanceUpdateAssStyle = MaintenanceUpdateAssStyleCheckBox.IsChecked == true;
            updated.MaintenanceUpdateFonts = MaintenanceUpdateFontsCheckBox.IsChecked == true;
            updated.MaintenanceApplyAudioSettings = MaintenanceApplyAudioSettingsCheckBox.IsChecked == true;
            updated.MaintenanceApplyVideoSettings = MaintenanceApplyVideoSettingsCheckBox.IsChecked == true;
            updated.MaintenanceRefreshTags = MaintenanceRefreshTagsCheckBox.IsChecked == true;
            updated.MaintenanceDetectLegacyAss = MaintenanceDetectLegacyAssCheckBox.IsChecked == true;
            updated.MaintenanceLegacyAssStyles = MaintenanceLegacyAssStylesTextBox.Text.Trim();
            updated.MaintenanceOutputPrefix = MaintenanceOutputPrefixTextBox.Text.Trim();
            updated.MaintenanceReplaceOriginal = MaintenanceReplaceOriginalCheckBox.IsChecked == true;
            updated.EnableLapseSync = LapseAutoSyncRadioButton.IsChecked == true;
            updated.EnableLapseValidationCheck = LapseValidationRadioButton.IsChecked == true;
            if (!double.TryParse(
                    LapseValidationWarningSecondsTextBox.Text,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out var lapseValidationWarningSeconds)
                && !double.TryParse(
                    LapseValidationWarningSecondsTextBox.Text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out lapseValidationWarningSeconds))
            {
                throw new InvalidOperationException(AppText.Get("Settings_LapseValidationThresholdError"));
            }
            updated.LapseValidationWarningSeconds = lapseValidationWarningSeconds;
            updated.LapseMode = Enum.TryParse<LapseSyncMode>(LapseModeComboBox.SelectedValue as string, out var lapseMode)
                ? lapseMode : LapseSyncMode.Auto;
            updated.LapseReference = UseEmbeddedSubtitleReferenceCheckBox.IsChecked == true
                ? LapseReferenceMode.Auto
                : LapseReferenceMode.AudioOnly;
            if (!int.TryParse(LapseSplitPenaltyTextBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var splitPenalty))
            {
                throw new InvalidOperationException(AppText.Get("Settings_LapsePenaltyError"));
            }
            updated.LapseSplitPenalty = splitPenalty;
            if (!int.TryParse(LapseConfidenceTextBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var confidenceThreshold))
            {
                throw new InvalidOperationException(AppText.Get("Settings_LapseConfidenceError"));
            }
            updated.LapseConfidenceThreshold = confidenceThreshold;
            updated.WarnOnLargeLapseCorrection = WarnOnLargeLapseCorrectionCheckBox.IsChecked == true;
            if (!double.TryParse(
                    LapseLargeCorrectionWarningSecondsTextBox.Text,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out var largeCorrectionWarningSeconds))
            {
                throw new InvalidOperationException(AppText.Get("Settings_LapseLargeCorrectionThresholdError"));
            }
            updated.LapseLargeCorrectionWarningSeconds = largeCorrectionWarningSeconds;
            updated.MaintenanceUpdateLapseSync = MaintenanceUpdateLapseSyncCheckBox.IsChecked == true;
            updated.MaintenanceForceLapseResync = MaintenanceForceLapseResyncCheckBox.IsChecked == true;
            updated.Validate();
            updated.Save();
            Settings = updated;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, AppText.Get("Settings_ValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToggleAssStyleDetails_Click(object sender, RoutedEventArgs e)
    {
        var expand = AssStyleDetailsPanel.Visibility != Visibility.Visible;
        AssStyleDetailsPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        AssStyleDetailsArrowTransform.Angle = expand ? 180 : 0;
        AssStyleDetailsToggleButton.ToolTip = AppText.Get(
            expand ? "Settings_CollapseAssStyle" : "Settings_ExpandAssStyle");
    }

    private void ToggleMaintenanceAdvancedStyles_Click(object sender, RoutedEventArgs e)
    {
        var expand = MaintenanceAdvancedStylesPanel.Visibility != Visibility.Visible;
        MaintenanceAdvancedStylesPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        MaintenanceAdvancedStylesArrowTransform.Angle = expand ? 180 : 0;
        MaintenanceAdvancedStylesToggleButton.ToolTip = AppText.Get(
            expand ? "Settings_CollapseAssStyle" : "Settings_ExpandAssStyle");
    }

    private void MaintenanceReplaceOriginal_Changed(object sender, RoutedEventArgs e) =>
        UpdateMaintenanceOutputControls();

    private void LapseModeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateLapseSplitPenaltyControls();

    private void LapseOperationMode_Changed(object sender, RoutedEventArgs e)
        => UpdateLapseOperationControls();

    private void UpdateLapseOperationControls()
    {
        if (LapseDisabledRadioButton is null || LapseAutoSyncRadioButton is null
            || LapseValidationRadioButton is null || LapseOptionsGrid is null
            || LapseWarningSection is null || LapseLargeCorrectionWarningPanel is null
            || LapseValidationThresholdGrid is null || LapseOperationDescriptionTextBlock is null)
        {
            return;
        }

        var syncEnabled = LapseAutoSyncRadioButton.IsChecked == true;
        var validationEnabled = LapseValidationRadioButton.IsChecked == true;
        var lapseEnabled = syncEnabled || validationEnabled;
        LapseOptionsGrid.IsEnabled = lapseEnabled;
        LapseWarningSection.Visibility = lapseEnabled ? Visibility.Visible : Visibility.Collapsed;
        LapseLargeCorrectionWarningPanel.Visibility = syncEnabled ? Visibility.Visible : Visibility.Collapsed;
        LapseValidationThresholdGrid.Visibility = validationEnabled ? Visibility.Visible : Visibility.Collapsed;
        LapseOperationDescriptionTextBlock.Text = AppText.Get(
            syncEnabled
                ? "Settings_LapseOperationAutomaticHelp"
                : validationEnabled
                    ? "Settings_LapseOperationValidationHelp"
                    : "Settings_LapseOperationDisabledHelp");
    }

    private void UpdateLapseSplitPenaltyControls()
    {
        if (LapseModeComboBox is null || LapseSplitPenaltyTextBox is null
            || LapseSplitPenaltyLabel is null || LapseSplitPenaltyHelpText is null)
        {
            return;
        }

        var enabled = string.Equals(
            LapseModeComboBox.SelectedValue as string,
            LapseSyncMode.Split.ToString(),
            StringComparison.Ordinal);
        LapseSplitPenaltyTextBox.IsEnabled = enabled;
        LapseSplitPenaltyLabel.IsEnabled = enabled;
        LapseSplitPenaltyHelpText.IsEnabled = enabled;
    }

    private void UpdateMaintenanceOutputControls()
    {
        if (MaintenanceOutputPrefixTextBox is not null && MaintenanceReplaceOriginalCheckBox is not null)
        {
            MaintenanceOutputPrefixTextBox.IsEnabled = MaintenanceReplaceOriginalCheckBox.IsChecked != true;
        }
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            Process.Start(new ProcessStartInfo(AppSettings.SettingsDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppText.Get("Settings_OpenFolderError", exception.Message),
                AppText.Get("Settings_OpenFolderErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OpenSubMuxSansFont_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            const string resourceName = "SubMuxBatch.Core.Resources.SubMuxSans-Medium.otf";
            var fontDirectory = Path.Combine(AppSettings.SettingsDirectory, "fonts");
            Directory.CreateDirectory(fontDirectory);
            var fontPath = Path.Combine(fontDirectory, "SubMuxSans-Medium.otf");
            await using (var source = typeof(AppSettings).Assembly.GetManifestResourceStream(resourceName)
                                      ?? throw new InvalidOperationException("The bundled SubMux Sans resource is missing."))
            await using (var destination = new FileStream(
                             fontPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.Read,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination);
            }

            var fontViewer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "fontview.exe");
            var startInfo = new ProcessStartInfo(fontViewer)
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(fontPath);
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppText.Get("Settings_OpenFontError", exception.Message),
                AppText.Get("Settings_OpenFontErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenSourceLicenses_Click(object sender, RoutedEventArgs e)
    {
        new OpenSourceLicensesWindow
        {
            Owner = this
        }.ShowDialog();
    }

    private async Task RefreshSubMuxSansStatusAsync()
    {
        if (_fontStatusRefreshInProgress || !IsLoaded)
        {
            return;
        }

        _fontStatusRefreshInProgress = true;
        try
        {
            var installed = await Task.Run(() =>
                new InstalledFontResolver().Resolve(new AssFontRequirement("SubMux Sans", 500, false)) is not null);
            SubMuxSansStatusText.Text = AppText.Get(
                installed ? "Settings_FontInstalled" : "Settings_FontNotInstalled");
        }
        catch
        {
            SubMuxSansStatusText.Text = AppText.Get("Settings_FontStatusUnknown");
        }
        finally
        {
            _fontStatusRefreshInProgress = false;
        }
    }

    private void OpenManualStyleInput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitAssStyleFields();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            ShowAssValidationError(exception.Message);
            return;
        }

        var dialog = new ManualAssStyleInputWindow(_styleDefinition.ToStyleLine())
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.StyleDefinition is not null)
        {
            _styleDefinition = dialog.StyleDefinition;
            _assStyleLine = _styleDefinition.ToStyleLine();
            PopulateAssStyleFields();
        }
    }

    private void UseDefaultAssStyle_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        _playResX = defaults.PlayResX;
        _playResY = defaults.PlayResY;
        _styleDefinition = AssStyleDefinition.Parse(AppSettings.DefaultAssStyleLine);
        _assStyleLine = _styleDefinition.ToStyleLine();
        UseCustomAssStyleCheckBox.IsChecked = true;
        PopulateAssStyleFields();
    }

    private void ResetAllSettings_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            AppText.Get("Settings_ResetAllSettingsConfirm"),
            AppText.Get("Settings_ResetAllSettingsTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var selectedPresetId = Settings.SelectedPresetId;
        Settings = new AppSettings { SelectedPresetId = selectedPresetId };
        LoadSettingsIntoControls(Settings);
    }

    private async void RepairBundledTools_Click(object sender, RoutedEventArgs e)
    {
        RepairBundledToolsButton.IsEnabled = false;
        BundledToolsStatusText.Text = AppText.Get("Settings_BundledToolsRepairing");
        try
        {
            var status = await new BundledToolManager().EnsureAvailableAsync();
            BundledToolsStatusText.Text = status.IsHealthy
                ? AppText.Get("Settings_BundledToolsReady")
                : AppText.Get("Settings_BundledToolsFailed", status.Error ?? "Unknown error");
            SetBundledToolsStatusAppearance(status.IsHealthy);
        }
        finally
        {
            RepairBundledToolsButton.IsEnabled = true;
        }
    }

    private void OpenBundledToolsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppSettings.SettingsDirectory, "tools");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppText.Get("Settings_OpenBundledToolsFolderError", exception.Message),
                AppText.Get("Settings_BundledTools"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ToolWebsite_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppText.Get("Settings_OpenToolWebsiteError", exception.Message),
                AppText.Get("Settings_BundledTools"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetBundledToolsStatusAppearance(bool healthy)
    {
        BundledToolsStatusText.Foreground = healthy
            ? (System.Windows.Media.Brush)FindResource("SecondaryTextBrush")
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 35, 24));
    }

    private void CommitAssStyleFields()
    {
        if (!TryParseResolution(PlayResXTextBox.Text, out var playResX)
            || !TryParseResolution(PlayResYTextBox.Text, out var playResY))
        {
            throw new InvalidOperationException(AppText.Get("Ass_ResolutionError"));
        }

        var updated = AssStyleDefinition.Parse(_styleDefinition.ToStyleLine());
        updated.FontName = FontNameTextBox.Text;
        updated.FontSize = ParseDouble(FontSizeTextBox.Text, AppText.Get("Ass_FontSize"));
        updated.PrimaryColour = PrimaryColorTextBox.Text;
        updated.OutlineColour = OutlineColorTextBox.Text;
        updated.BackColour = BackColorTextBox.Text;
        updated.Bold = BoldCheckBox.IsChecked == true;
        updated.Italic = ItalicCheckBox.IsChecked == true;
        updated.Outline = ParseDouble(OutlineWidthTextBox.Text, AppText.Get("Ass_OutlineWidth"));
        updated.Shadow = ParseDouble(ShadowDepthTextBox.Text, AppText.Get("Ass_ShadowDepth"));
        updated.Alignment = AlignmentComboBox.SelectedItem is AlignmentOption alignment
            ? alignment.Value
            : throw new FormatException(AppText.Get("Ass_AlignmentError"));
        updated.MarginLeft = ParseInteger(MarginLeftTextBox.Text, AppText.Get("Ass_MarginLeft"));
        updated.MarginRight = ParseInteger(MarginRightTextBox.Text, AppText.Get("Ass_MarginRight"));
        updated.MarginVertical = ParseInteger(MarginVerticalTextBox.Text, AppText.Get("Ass_MarginVertical"));
        updated.Validate();

        var styleLine = updated.ToStyleLine();
        new AppSettings
        {
            UseCustomAssStyle = true,
            PlayResX = playResX,
            PlayResY = playResY,
            AssStyleLine = styleLine
        }.Validate();

        _styleDefinition = updated;
        _playResX = playResX;
        _playResY = playResY;
        _assStyleLine = styleLine;
    }

    private void PopulateAssStyleFields()
    {
        PlayResXTextBox.Text = _playResX.ToString(CultureInfo.InvariantCulture);
        PlayResYTextBox.Text = _playResY.ToString(CultureInfo.InvariantCulture);
        FontNameTextBox.Text = _styleDefinition.FontName;
        FontSizeTextBox.Text = FormatNumber(_styleDefinition.FontSize);
        PrimaryColorTextBox.Text = _styleDefinition.PrimaryColour;
        OutlineColorTextBox.Text = _styleDefinition.OutlineColour;
        BackColorTextBox.Text = _styleDefinition.BackColour;
        BoldCheckBox.IsChecked = _styleDefinition.Bold;
        ItalicCheckBox.IsChecked = _styleDefinition.Italic;
        OutlineWidthTextBox.Text = FormatNumber(_styleDefinition.Outline);
        ShadowDepthTextBox.Text = FormatNumber(_styleDefinition.Shadow);
        AlignmentComboBox.SelectedItem = _alignmentOptions.First(
            option => option.Value == _styleDefinition.Alignment);
        MarginLeftTextBox.Text = _styleDefinition.MarginLeft.ToString(CultureInfo.InvariantCulture);
        MarginRightTextBox.Text = _styleDefinition.MarginRight.ToString(CultureInfo.InvariantCulture);
        MarginVerticalTextBox.Text = _styleDefinition.MarginVertical.ToString(CultureInfo.InvariantCulture);
    }

    private void ShowAssValidationError(string message) =>
        MessageBox.Show(
            this,
            message,
            AppText.Get("Ass_ValidationTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double ParseDouble(string value, string label)
    {
        if ((double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var result)
             || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            && double.IsFinite(result))
        {
            return result;
        }

        throw new FormatException(AppText.Get("Ass_NumberError", label));
    }

    private static int ParseInteger(string value, string label)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var result))
        {
            return result;
        }

        throw new FormatException(AppText.Get("Ass_IntegerError", label));
    }

    private static bool TryParseResolution(string value, out int resolution) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out resolution)
        && resolution is >= 16 and <= 16384;

    private void AudioSettings_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAudioEncodingControls();

    private void VideoSettings_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateVideoEncodingControls();

    private void UpdateAudioEncodingControls()
    {
        if (AudioProcessingModeComboBox is null || AudioCodecComboBox is null)
        {
            return;
        }
        var enabled = !string.Equals(
            AudioProcessingModeComboBox.SelectedValue as string,
            AudioProcessingMode.KeepOriginal.ToString(),
            StringComparison.Ordinal);
        AudioCodecComboBox.IsEnabled = enabled;
        AudioChannelModeComboBox.IsEnabled = enabled;
        AudioBitrateComboBox.IsEnabled = enabled;
    }

    private void UpdateVideoEncodingControls()
    {
        if (VideoProcessingModeComboBox is null || VideoEncodingOptionsPanel is null)
        {
            return;
        }
        var enabled = !string.Equals(
            VideoProcessingModeComboBox.SelectedValue as string,
            VideoProcessingMode.KeepOriginal.ToString(),
            StringComparison.Ordinal);
        VideoEncodingOptionsPanel.IsEnabled = enabled;
        VideoCpuOptionsCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        CustomVideoOptionsCard.Visibility = enabled
            && string.Equals(
                VideoQualityProfileComboBox.SelectedValue as string,
                VideoQualityProfile.Custom.ToString(),
                StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (CustomVideoRateControlComboBox is not null)
        {
            var constantQuality = string.Equals(
                CustomVideoRateControlComboBox.SelectedValue as string,
                VideoRateControlMode.ConstantQuality.ToString(),
                StringComparison.Ordinal);
            CustomX265CrfTextBox.IsEnabled = constantQuality;
            CustomVideoBitrateComboBox.IsEnabled = !constantQuality;
        }
        if (VideoCpuUsageComboBox is not null)
        {
            CustomVideoThreadCountTextBox.IsEnabled = string.Equals(
                VideoCpuUsageComboBox.SelectedValue as string,
                VideoCpuUsageMode.Custom.ToString(),
                StringComparison.Ordinal);
        }
    }

    private static TEnum ParseSelectedEnum<TEnum>(ComboBox comboBox, string errorKey)
        where TEnum : struct, Enum
    {
        if (comboBox.SelectedValue is string selected
            && Enum.TryParse<TEnum>(selected, out var value)
            && Enum.IsDefined(value))
        {
            return value;
        }
        throw new InvalidOperationException(AppText.Get(errorKey));
    }

    private static AssStyleDefinition ParseStyleOrDefault(string? styleLine) =>
        AssStyleDefinition.TryParse(styleLine, out var definition)
            ? definition!
            : AssStyleDefinition.Parse(AppSettings.DefaultAssStyleLine);


    private sealed record AlignmentOption(int Value, string Label)
    {
        public override string ToString() => $"{Value} · {Label}";
    }
}
