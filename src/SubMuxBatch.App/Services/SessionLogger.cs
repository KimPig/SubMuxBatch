using System.Text;
using System.IO;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.App.Services;

public sealed class SessionLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public SessionLogger()
    {
        LogDirectory = Path.Combine(AppSettings.SettingsDirectory, "logs");
        Directory.CreateDirectory(LogDirectory);
        LogPath = Path.Combine(LogDirectory, $"submux-batch-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(LogPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string LogDirectory { get; }
    public string LogPath { get; }

    public string Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_gate)
        {
            _writer.WriteLine(line);
        }

        return line;
    }

    public void Dispose() => _writer.Dispose();
}
