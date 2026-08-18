using System.ComponentModel;
using System.Windows;
using SubMuxBatch.App.Localization;
using SubMuxBatch.Core.Updates;

namespace SubMuxBatch.App;

public partial class UpdateWindow : Window
{
    private readonly Func<IProgress<UpdateDownloadProgress>, CancellationToken, Task> _updateAction;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _isUpdating;
    private bool _allowClose;

    public UpdateWindow(
        UpdateVersion currentVersion,
        UpdateRelease release,
        Func<IProgress<UpdateDownloadProgress>, CancellationToken, Task> updateAction)
    {
        InitializeComponent();
        _updateAction = updateAction;
        Title = AppText.Get("Update_Title");
        HeadingText.Text = AppText.Get("Update_Heading");
        DescriptionText.Text = AppText.Get("Update_Description");
        CurrentVersionLabel.Text = AppText.Get("Update_CurrentVersion");
        CurrentVersionText.Text = $"v{currentVersion}";
        LatestVersionLabel.Text = AppText.Get("Update_LatestVersion");
        LatestVersionText.Text = $"v{release.Version}";
        LaterButton.Content = AppText.Get("Update_Later");
        UpdateButton.Content = AppText.Get("Update_Install");
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        DialogResult = false;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;
        LaterButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressStatusText.Text = AppText.Get("Update_Downloading");
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 0;

        var progress = new Progress<UpdateDownloadProgress>(update =>
        {
            DownloadProgressBar.Value = update.Percentage;
            ProgressStatusText.Text = AppText.Get(
                "Update_DownloadProgress",
                update.Percentage,
                FormatMegabytes(update.BytesReceived),
                FormatMegabytes(update.TotalBytes));
        });

        try
        {
            await _updateAction(progress, _cancellation.Token);
            ProgressStatusText.Text = AppText.Get("Update_Restarting");
            DownloadProgressBar.Value = 100;
            _allowClose = true;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _isUpdating = false;
            LaterButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
            ErrorText.Text = AppText.Get("Update_Failed", exception.Message);
            ErrorText.Visibility = Visibility.Visible;
            ProgressStatusText.Text = AppText.Get("Update_DownloadFailed");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isUpdating && !_allowClose)
        {
            e.Cancel = true;
        }
    }

    private static string FormatMegabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.0");
}
