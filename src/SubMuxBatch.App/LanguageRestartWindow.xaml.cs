using System.Windows;
using SubMuxBatch.App.Services;

namespace SubMuxBatch.App;

public partial class LanguageRestartWindow : Window
{
    public LanguageRestartWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => WindowPlacementHelper.FitToCurrentWorkingArea(this);
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}
