using System.Windows;
using System.Windows.Media;
using SubMuxBatch.App.Localization;

namespace SubMuxBatch.App;

public partial class UpdateStatusWindow : Window
{
    public UpdateStatusWindow(
        string title,
        string heading,
        string description,
        string? details = null,
        bool isError = false)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = heading;
        DescriptionText.Text = description;
        CloseButton.Content = AppText.Get("Common_Close");
        if (!string.IsNullOrWhiteSpace(details))
        {
            DetailsText.Text = details;
            DetailsText.Visibility = Visibility.Visible;
        }

        if (isError)
        {
            AccentBar.Background = new SolidColorBrush(Color.FromRgb(196, 43, 28));
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
