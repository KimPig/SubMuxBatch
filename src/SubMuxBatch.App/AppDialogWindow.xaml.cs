using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using SubMuxBatch.App.Localization;

namespace SubMuxBatch.App;

public enum AppDialogKind
{
    Information,
    Warning,
    Error,
    Question
}

public enum AppDialogButtons
{
    Ok,
    YesNo,
    YesNoCancel
}

public enum AppDialogResult
{
    None,
    Ok,
    Yes,
    No,
    Cancel
}

public static class AppDialog
{
    public static AppDialogResult Show(
        Window? owner,
        string message,
        string title,
        AppDialogKind kind = AppDialogKind.Information,
        AppDialogButtons buttons = AppDialogButtons.Ok,
        string? primaryText = null,
        string? secondaryText = null,
        string? tertiaryText = null)
    {
        var dialog = new AppDialogWindow(message, title, kind, buttons, primaryText, secondaryText, tertiaryText);
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog.Result;
    }
}

// Keep app-created alerts visually consistent even when existing or future code
// uses the familiar MessageBox.Show API inside the SubMuxBatch.App namespace.
public static class MessageBox
{
    public static System.Windows.MessageBoxResult Show(
        string messageBoxText,
        string caption,
        System.Windows.MessageBoxButton button,
        System.Windows.MessageBoxImage icon) =>
        ShowCore(null, messageBoxText, caption, button, icon, System.Windows.MessageBoxResult.None);

    public static System.Windows.MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        System.Windows.MessageBoxButton button,
        System.Windows.MessageBoxImage icon) =>
        ShowCore(owner, messageBoxText, caption, button, icon, System.Windows.MessageBoxResult.None);

    public static System.Windows.MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        System.Windows.MessageBoxButton button,
        System.Windows.MessageBoxImage icon,
        System.Windows.MessageBoxResult defaultResult) =>
        ShowCore(owner, messageBoxText, caption, button, icon, defaultResult);

    private static System.Windows.MessageBoxResult ShowCore(
        Window? owner,
        string messageBoxText,
        string caption,
        System.Windows.MessageBoxButton button,
        System.Windows.MessageBoxImage icon,
        System.Windows.MessageBoxResult defaultResult)
    {
        var kind = icon switch
        {
            System.Windows.MessageBoxImage.Error => AppDialogKind.Error,
            System.Windows.MessageBoxImage.Warning => AppDialogKind.Warning,
            System.Windows.MessageBoxImage.Question => AppDialogKind.Question,
            _ => AppDialogKind.Information
        };
        var buttons = button == System.Windows.MessageBoxButton.YesNo
            ? AppDialogButtons.YesNo
            : AppDialogButtons.Ok;
        var result = AppDialog.Show(owner, messageBoxText, caption, kind, buttons);
        return result switch
        {
            AppDialogResult.Yes => System.Windows.MessageBoxResult.Yes,
            AppDialogResult.No => System.Windows.MessageBoxResult.No,
            AppDialogResult.Ok => System.Windows.MessageBoxResult.OK,
            _ when defaultResult != System.Windows.MessageBoxResult.None => defaultResult,
            _ => System.Windows.MessageBoxResult.None
        };
    }
}

public partial class AppDialogWindow : Window
{
    private readonly AppDialogButtons _buttons;
    private bool _closingFromButton;

    public AppDialogWindow(
        string message,
        string title,
        AppDialogKind kind,
        AppDialogButtons buttons,
        string? primaryText,
        string? secondaryText,
        string? tertiaryText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        _buttons = buttons;

        (IconText.Text, IconBorder.Background) = kind switch
        {
            AppDialogKind.Error => ("×", BrushFromRgb(196, 43, 28)),
            AppDialogKind.Warning => ("!", BrushFromRgb(202, 122, 0)),
            AppDialogKind.Question => ("?", BrushFromRgb(0, 120, 212)),
            _ => ("i", BrushFromRgb(0, 120, 212))
        };

        if (buttons is AppDialogButtons.YesNo or AppDialogButtons.YesNoCancel)
        {
            PrimaryButton.Content = primaryText ?? AppText.Get("Common_Yes");
            SecondaryButton.Content = secondaryText ?? AppText.Get("Common_No");
            SecondaryButton.Visibility = Visibility.Visible;
            if (buttons == AppDialogButtons.YesNoCancel)
            {
                TertiaryButton.Content = tertiaryText ?? AppText.Get("Common_Cancel");
                TertiaryButton.Visibility = Visibility.Visible;
                TertiaryButton.IsCancel = true;
            }
            else
            {
                PrimaryButton.IsDefault = false;
                SecondaryButton.IsDefault = true;
                SecondaryButton.IsCancel = true;
                SecondaryButton.Margin = new Thickness(0);
            }
        }
        else
        {
            PrimaryButton.Content = primaryText ?? AppText.Get("Common_OK");
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
    }

    public AppDialogResult Result { get; private set; }

    private static SolidColorBrush BrushFromRgb(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = _buttons is AppDialogButtons.YesNo or AppDialogButtons.YesNoCancel
            ? AppDialogResult.Yes
            : AppDialogResult.Ok;
        _closingFromButton = true;
        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = AppDialogResult.No;
        _closingFromButton = true;
        Close();
    }

    private void TertiaryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = AppDialogResult.Cancel;
        _closingFromButton = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingFromButton && Result == AppDialogResult.None)
        {
            Result = _buttons switch
            {
                AppDialogButtons.YesNo => AppDialogResult.No,
                AppDialogButtons.YesNoCancel => AppDialogResult.Cancel,
                _ => AppDialogResult.Ok
            };
        }

        base.OnClosing(e);
    }
}
