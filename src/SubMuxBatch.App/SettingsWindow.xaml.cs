using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SubMuxBatch.App.Localization;
using SubMuxBatch.App.Services;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;

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
    private string _assStyleLine;
    private AssStyleDefinition _styleDefinition;
    private readonly DependencyLocator _dependencyLocator = new();

    public SettingsWindow(AppSettings settings, DependencyReport? detectedDependencies = null)
    {
        InitializeComponent();
        Settings = settings;

        LanguageComboBox.SelectedValue = settings.Language.ToString();
        if (LanguageComboBox.SelectedIndex < 0)
        {
            LanguageComboBox.SelectedValue = AppLanguage.System.ToString();
        }

        detectedDependencies ??= new DependencyLocator().Locate(
            settings.MkvMergePath,
            settings.SeConvPath);
        MkvMergePathTextBox.Text = detectedDependencies.MkvMerge.Path
                                   ?? settings.MkvMergePath
                                   ?? string.Empty;
        SeConvPathTextBox.Text = detectedDependencies.SeConv.Path
                                ?? settings.SeConvPath
                                ?? string.Empty;
        OutputPrefixTextBox.Text = settings.OutputPrefix;
        IncludeSubdirectoriesCheckBox.IsChecked = settings.IncludeSubdirectories;
        AllowSubtitleSuffixMatchCheckBox.IsChecked = settings.AllowSubtitleSuffixMatch;
        RemoveExistingSubtitlesCheckBox.IsChecked = settings.RemoveExistingSubtitles;
        RemoveExistingFontAttachmentsCheckBox.IsChecked = settings.RemoveExistingFontAttachments;
        AttachAssStyleFontsCheckBox.IsChecked = settings.AttachAssStyleFonts;
        FilterAudioTracksByLanguageCheckBox.IsChecked = settings.FilterAudioTracksByLanguage;
        AudioLanguageComboBox.SelectedValue = settings.SelectedAudioLanguage.ToString();
        if (AudioLanguageComboBox.SelectedIndex < 0)
        {
            AudioLanguageComboBox.SelectedValue = AudioTrackLanguage.Japanese.ToString();
        }
        ConcurrentJobCountComboBox.SelectedValue = settings.ConcurrentJobCount.ToString();
        if (ConcurrentJobCountComboBox.SelectedIndex < 0)
        {
            ConcurrentJobCountComboBox.SelectedValue = AppSettings.MinConcurrentJobCount.ToString();
        }
        ShowCompletionNotificationCheckBox.IsChecked = settings.ShowCompletionNotification;
        PlayCompletionSoundCheckBox.IsChecked = settings.PlayCompletionSound;
        UseCustomAssStyleCheckBox.IsChecked = settings.UseCustomAssStyle;
        _playResX = settings.PlayResX;
        _playResY = settings.PlayResY;
        _styleDefinition = ParseStyleOrDefault(settings.AssStyleLine);
        _assStyleLine = _styleDefinition.ToStyleLine();
        AlignmentComboBox.ItemsSource = _alignmentOptions;
        PopulateAssStyleFields();

        Loaded += (_, _) => WindowPlacementHelper.FitToCurrentWorkingArea(this);
    }

    public AppSettings Settings { get; private set; }

    private void BrowseMkvMerge_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutable(MkvMergePathTextBox, $"mkvmerge.exe|mkvmerge.exe|{AppText.Get("Common_Executable")}|*.exe");

    private void BrowseSeConv_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutable(SeConvPathTextBox, $"seconv.exe|seconv.exe|{AppText.Get("Common_Executable")}|*.exe");

    private void AutoDetectMkvMerge_Click(object sender, RoutedEventArgs e)
    {
        var dependency = _dependencyLocator.Locate(
            configuredMkvMerge: null,
            EmptyToNull(SeConvPathTextBox.Text)).MkvMerge;
        ApplyDetectedPath(MkvMergePathTextBox, dependency);
    }

    private void AutoDetectSeConv_Click(object sender, RoutedEventArgs e)
    {
        var dependency = _dependencyLocator.Locate(
            EmptyToNull(MkvMergePathTextBox.Text),
            configuredSeConv: null).SeConv;
        ApplyDetectedPath(SeConvPathTextBox, dependency);
    }

    private void ApplyDetectedPath(
        System.Windows.Controls.TextBox target,
        ToolDependency dependency)
    {
        if (dependency.Path is not null)
        {
            target.Text = dependency.Path;
            target.CaretIndex = target.Text.Length;
            return;
        }

        MessageBox.Show(
            this,
            AppText.Get("Settings_AutoDetectFailed", dependency.ExecutableName),
            AppText.Get("Settings_AutoDetectTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void BrowseExecutable(System.Windows.Controls.TextBox target, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
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
            updated.MkvMergePath = EmptyToNull(MkvMergePathTextBox.Text);
            updated.SeConvPath = EmptyToNull(SeConvPathTextBox.Text);
            updated.OutputPrefix = OutputPrefixTextBox.Text.Trim();
            updated.IncludeSubdirectories = IncludeSubdirectoriesCheckBox.IsChecked == true;
            updated.AllowSubtitleSuffixMatch = AllowSubtitleSuffixMatchCheckBox.IsChecked == true;
            updated.RemoveExistingSubtitles = RemoveExistingSubtitlesCheckBox.IsChecked == true;
            updated.RemoveExistingFontAttachments = RemoveExistingFontAttachmentsCheckBox.IsChecked == true;
            updated.AttachAssStyleFonts = AttachAssStyleFontsCheckBox.IsChecked == true;
            updated.FilterAudioTracksByLanguage = FilterAudioTracksByLanguageCheckBox.IsChecked == true;
            if (AudioLanguageComboBox.SelectedValue is not string selectedAudioLanguage
                || !Enum.TryParse(selectedAudioLanguage, out AudioTrackLanguage audioLanguage)
                || !Enum.IsDefined(audioLanguage))
            {
                throw new InvalidOperationException(AppText.Get("Settings_SelectAudioLanguageError"));
            }
            updated.SelectedAudioLanguage = audioLanguage;
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

    private static AssStyleDefinition ParseStyleOrDefault(string? styleLine) =>
        AssStyleDefinition.TryParse(styleLine, out var definition)
            ? definition!
            : AssStyleDefinition.Parse(AppSettings.DefaultAssStyleLine);

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AlignmentOption(int Value, string Label)
    {
        public override string ToString() => $"{Value} · {Label}";
    }
}
