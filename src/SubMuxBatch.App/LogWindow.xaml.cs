using System.Diagnostics;
using System.IO;
using System.Windows;
using SubMuxBatch.App.Localization;
using SubMuxBatch.App.Services;

namespace SubMuxBatch.App;

public partial class LogWindow : Window
{
    private readonly string _logDirectory;

    public LogWindow(
        string initialText,
        string logPath,
        string logDirectory,
        string? heading = null,
        string? description = null)
    {
        InitializeComponent();
        _logDirectory = logDirectory;
        HeadingText.Text = heading ?? AppText.Get("Log_Heading");
        DescriptionText.Text = description ?? AppText.Get("Log_Description");
        Title = $"SubMux Batch - {HeadingText.Text}";
        LogTextBox.Text = initialText;
        LogPathText.Text = logPath;
        LogPathText.ToolTip = logPath;
        Loaded += (_, _) =>
        {
            WindowPlacementHelper.FitToCurrentWorkingArea(this);
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
            LogTextBox.ScrollToEnd();
        };
    }

    public void AppendLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => AppendLine(line));
            return;
        }

        LogTextBox.AppendText(line + Environment.NewLine);
        if (LogTextBox.Text.Length > 200_000)
        {
            LogTextBox.Text = LogTextBox.Text[^150_000..];
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
        }

        LogTextBox.ScrollToEnd();
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (LogTextBox.Text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(LogTextBox.Text);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppText.Get("Log_CopyFailed", exception.Message),
                AppText.Get("Log_CopyTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_logDirectory);
        Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
