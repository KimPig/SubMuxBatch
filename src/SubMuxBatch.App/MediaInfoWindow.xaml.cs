using System.Windows;
using SubMuxBatch.App.Localization;
using SubMuxBatch.App.Services;
using SubMuxBatch.App.ViewModels;

namespace SubMuxBatch.App;

public partial class MediaInfoWindow : Window
{
    public MediaInfoWindow(QueueItemViewModel item)
    {
        InitializeComponent();
        DataContext = item;
        HeadingText.Text = AppText.Get("MediaDetails_Title", item.Name);
        Title = $"SubMux Batch - {HeadingText.Text}";
        Loaded += (_, _) => WindowPlacementHelper.FitToCurrentWorkingArea(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
