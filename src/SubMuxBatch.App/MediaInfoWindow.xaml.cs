using System.Windows;
using SubMuxBatch.App.Localization;
using SubMuxBatch.App.Services;
using SubMuxBatch.App.ViewModels;

namespace SubMuxBatch.App;

public partial class MediaInfoWindow : Window
{
    private readonly MediaInfoDetailsViewModel _viewModel;

    public MediaInfoWindow(QueueItemViewModel item)
    {
        InitializeComponent();
        _viewModel = new MediaInfoDetailsViewModel(item);
        DataContext = _viewModel;
        HeadingText.Text = AppText.Get("MediaDetails_Title", item.FileName);
        Title = $"SubMux Batch - {HeadingText.Text}";
        Loaded += (_, _) => WindowPlacementHelper.FitToCurrentWorkingArea(this);
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.CopyText);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppText.Get("MediaDetails_CopyFailed", exception.Message),
                AppText.Get("MediaDetails_CopyTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
