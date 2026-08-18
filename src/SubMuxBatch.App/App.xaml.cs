using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SubMuxBatch.App.Localization;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Updates;

namespace SubMuxBatch.App;

public partial class App : Application
{
    private readonly CultureInfo _systemUiCulture = CultureInfo.CurrentUICulture;

    protected override void OnStartup(StartupEventArgs e)
    {
        var settings = AppSettings.Load();
        var culture = AppLanguageResolver.Resolve(settings.Language, _systemUiCulture);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        base.OnStartup(e);
        if (SelfUpdateCommand.IsApplyMode(e.Args))
        {
            ApplyUpdateAndExit(e.Args);
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        var cleanupRoot = UpdateStorage.TryGetCleanupRoot(e.Args, out var requestedCleanupRoot)
            ? requestedCleanupRoot
            : null;
        _ = UpdateStorage.CleanupAsync(cleanupRoot);
    }

    private static void ApplyUpdateAndExit(IReadOnlyList<string> arguments)
    {
        SelfUpdateCommand? command = null;
        try
        {
            if (!SelfUpdateCommand.TryParse(arguments, out command) || command is null)
            {
                throw new InvalidOperationException("The update command is incomplete.");
            }

            SelfUpdateApplier.ApplyAsync(command).GetAwaiter().GetResult();
            Current.Shutdown();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                AppText.Get("Update_ApplyFailed", exception.Message),
                AppText.Get("Update_ApplyFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            if (command is not null && File.Exists(command.TargetExecutablePath))
            {
                var targetExecutablePath = command.TargetExecutablePath;
                try
                {
                    Process.Start(new ProcessStartInfo(targetExecutablePath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(targetExecutablePath)
                                           ?? Environment.CurrentDirectory
                    });
                }
                catch
                {
                }
            }

            Current.Shutdown(1);
        }
    }

    public static bool RequiresLanguageRestart(AppLanguage language)
    {
        var app = (App)Current;
        var targetCulture = AppLanguageResolver.Resolve(language, app._systemUiCulture);
        return !string.Equals(
            targetCulture.Name,
            CultureInfo.CurrentUICulture.Name,
            StringComparison.OrdinalIgnoreCase);
    }
}
