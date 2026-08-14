using System.Windows;
using SubMuxBatch.App.Localization;
using SubMuxBatch.App.Services;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.App;

public partial class ManualAssStyleInputWindow : Window
{
    public ManualAssStyleInputWindow(string styleLine)
    {
        InitializeComponent();
        StyleTextBox.Text = styleLine;
        StyleTextBox.SelectAll();

        Loaded += (_, _) =>
        {
            WindowPlacementHelper.FitToCurrentWorkingArea(this);
            StyleTextBox.Focus();
        };
    }

    public AssStyleDefinition? StyleDefinition { get; private set; }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AssStyleDefinition.TryParse(StyleTextBox.Text.Trim(), out var definition, out var error))
        {
            MessageBox.Show(
                this,
                error ?? AppText.Get("ManualStyle_Invalid"),
                AppText.Get("ManualStyle_ValidationTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(definition!.Name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                AppText.Get("ManualStyle_DefaultOnly"),
                AppText.Get("ManualStyle_ValidationTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        StyleDefinition = definition;
        DialogResult = true;
    }
}
