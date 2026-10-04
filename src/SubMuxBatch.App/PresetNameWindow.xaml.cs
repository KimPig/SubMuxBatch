using System.Windows;
using System.Windows.Input;

namespace SubMuxBatch.App;

public partial class PresetNameWindow : Window
{
    public PresetNameWindow(string title, string initialName)
    {
        InitializeComponent();
        Title = title;
        NameTextBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public string? PresetName { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e) => Accept();

    private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Accept();
    }

    private void Accept()
    {
        var value = NameTextBox.Text.Trim();
        if (value.Length == 0)
        {
            NameTextBox.Focus();
            return;
        }

        PresetName = value;
        DialogResult = true;
    }
}
