using System.Windows;
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
                error ?? "ASS Style 값을 확인해 주세요.",
                "Style 입력 확인",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(definition!.Name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "Default 스타일만 사용할 수 있습니다. Style 이름을 Default로 입력해 주세요.",
                "Style 입력 확인",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        StyleDefinition = definition;
        DialogResult = true;
    }
}
