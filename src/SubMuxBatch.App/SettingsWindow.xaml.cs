using System.Windows;
using Microsoft.Win32;
using SubMuxBatch.App.Services;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.App;

public partial class SettingsWindow : Window
{
    private int _playResX;
    private int _playResY;
    private string _assStyleLine;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = settings;

        MkvMergePathTextBox.Text = settings.MkvMergePath ?? string.Empty;
        SeConvPathTextBox.Text = settings.SeConvPath ?? string.Empty;
        OutputPrefixTextBox.Text = settings.OutputPrefix;
        IncludeSubdirectoriesCheckBox.IsChecked = settings.IncludeSubdirectories;
        AllowSubtitleSuffixMatchCheckBox.IsChecked = settings.AllowSubtitleSuffixMatch;
        RemoveExistingSubtitlesCheckBox.IsChecked = settings.RemoveExistingSubtitles;
        RemoveExistingFontAttachmentsCheckBox.IsChecked = settings.RemoveExistingFontAttachments;
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
        _assStyleLine = settings.AssStyleLine;

        Loaded += (_, _) => WindowPlacementHelper.FitToCurrentWorkingArea(this);
    }

    public AppSettings Settings { get; private set; }

    private void BrowseMkvMerge_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutable(MkvMergePathTextBox, "mkvmerge.exe|mkvmerge.exe|실행 파일|*.exe");

    private void BrowseSeConv_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutable(SeConvPathTextBox, "seconv.exe|seconv.exe|실행 파일|*.exe");

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
            updated.MkvMergePath = EmptyToNull(MkvMergePathTextBox.Text);
            updated.SeConvPath = EmptyToNull(SeConvPathTextBox.Text);
            updated.OutputPrefix = OutputPrefixTextBox.Text.Trim();
            updated.IncludeSubdirectories = IncludeSubdirectoriesCheckBox.IsChecked == true;
            updated.AllowSubtitleSuffixMatch = AllowSubtitleSuffixMatchCheckBox.IsChecked == true;
            updated.RemoveExistingSubtitles = RemoveExistingSubtitlesCheckBox.IsChecked == true;
            updated.RemoveExistingFontAttachments = RemoveExistingFontAttachmentsCheckBox.IsChecked == true;
            updated.FilterAudioTracksByLanguage = FilterAudioTracksByLanguageCheckBox.IsChecked == true;
            if (AudioLanguageComboBox.SelectedValue is not string selectedAudioLanguage
                || !Enum.TryParse(selectedAudioLanguage, out AudioTrackLanguage audioLanguage)
                || !Enum.IsDefined(audioLanguage))
            {
                throw new InvalidOperationException(
                    "오디오 언어 필터에 사용할 언어를 영어, 일본어 또는 한국어 중에서 선택해 주세요.");
            }
            updated.SelectedAudioLanguage = audioLanguage;
            if (ConcurrentJobCountComboBox.SelectedValue is not string concurrentJobCountText
                || !int.TryParse(concurrentJobCountText, out var concurrentJobCount))
            {
                throw new InvalidOperationException("동시 작업 수를 선택해 주세요.");
            }
            updated.ConcurrentJobCount = concurrentJobCount;
            updated.ShowCompletionNotification = ShowCompletionNotificationCheckBox.IsChecked == true;
            updated.PlayCompletionSound = PlayCompletionSoundCheckBox.IsChecked == true;
            updated.UseCustomAssStyle = UseCustomAssStyleCheckBox.IsChecked == true;
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
            MessageBox.Show(this, exception.Message, "설정 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenAssStyleDetails_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AssStyleDetailsWindow(_playResX, _playResY, _assStyleLine)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _playResX = dialog.PlayResX;
            _playResY = dialog.PlayResY;
            _assStyleLine = dialog.AssStyleLine;
        }
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
