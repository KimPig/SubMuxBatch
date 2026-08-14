using System.Diagnostics;
using System.IO;
using System.Windows;
using SubMuxBatch.App.Services;

namespace SubMuxBatch.App;

public partial class LogWindow : Window
{
    private readonly string _logDirectory;

    public LogWindow(string initialText, string logPath, string logDirectory)
    {
        InitializeComponent();
        _logDirectory = logDirectory;
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
                $"로그를 클립보드에 복사하지 못했습니다.\n{exception.Message}",
                "로그 복사",
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
