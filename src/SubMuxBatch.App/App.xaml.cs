using System.Globalization;
using System.Windows;
using SubMuxBatch.Core.Configuration;

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
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
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
