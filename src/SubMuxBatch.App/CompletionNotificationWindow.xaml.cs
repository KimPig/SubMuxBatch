using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SubMuxBatch.App.Services;

namespace SubMuxBatch.App;

public enum CompletionNotificationKind
{
    Success,
    Warning,
    Failure
}

public partial class CompletionNotificationWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private static readonly TimeSpan VisibleDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(180);

    private readonly Window _anchor;
    private readonly DispatcherTimer _closeTimer;
    private HwndSource? _source;
    private bool _isDismissing;

    public CompletionNotificationWindow(
        Window anchor,
        string title,
        string summary,
        CompletionNotificationKind kind)
    {
        InitializeComponent();
        _anchor = anchor;
        TitleText.Text = title;
        SummaryText.Text = summary;
        ApplyKind(kind);

        _closeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = VisibleDuration
        };
        _closeTimer.Tick += CloseTimer_Tick;
        SourceInitialized += Window_SourceInitialized;
        ContentRendered += Window_ContentRendered;
        Closed += Window_Closed;
    }

    public void DismissImmediately()
    {
        _isDismissing = true;
        _closeTimer.Stop();
        Close();
    }

    private void ApplyKind(CompletionNotificationKind kind)
    {
        var (accent, iconBackground, glyph) = kind switch
        {
            CompletionNotificationKind.Warning => (
                Color.FromRgb(138, 109, 0),
                Color.FromRgb(255, 244, 206),
                Geometry.Parse("M 8,1.5 L 15,14.5 H 1 Z M 8,5.5 V 10 M 8,12.5 V 12.6")),
            CompletionNotificationKind.Failure => (
                Color.FromRgb(232, 17, 35),
                Color.FromRgb(253, 231, 233),
                Geometry.Parse("M 3.5,3.5 L 12.5,12.5 M 12.5,3.5 L 3.5,12.5")),
            _ => (
                Color.FromRgb(16, 124, 16),
                Color.FromRgb(223, 246, 221),
                Geometry.Parse("M 2,8.5 L 6,12.5 L 14,4.5"))
        };

        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();
        var iconBackgroundBrush = new SolidColorBrush(iconBackground);
        iconBackgroundBrush.Freeze();
        AccentBar.Background = accentBrush;
        StatusBadge.Background = iconBackgroundBrush;
        StatusGlyph.Stroke = accentBrush;
        StatusGlyph.Data = glyph;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate));
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmMouseActivate)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(MaNoActivate);
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        WindowPlacementHelper.PlaceBottomRight(this, _anchor);
        BeginEntranceAnimation();
        _closeTimer.Start();
    }

    private void BeginEntranceAnimation()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, AnimationDuration) { EasingFunction = easing });
        SlideTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, AnimationDuration) { EasingFunction = easing });
    }

    private void BeginDismissAnimation()
    {
        if (_isDismissing)
        {
            return;
        }

        _isDismissing = true;
        _closeTimer.Stop();
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(Opacity, 0, AnimationDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            if (IsLoaded)
            {
                Close();
            }
        };
        BeginAnimation(OpacityProperty, fade);
        SlideTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(SlideTransform.Y, 6, AnimationDuration)
            {
                EasingFunction = easing
            });
    }

    private void RestoreAnchor()
    {
        if (!_anchor.IsVisible)
        {
            _anchor.Show();
        }

        if (_anchor.WindowState == WindowState.Minimized)
        {
            _anchor.WindowState = WindowState.Normal;
        }

        _anchor.Activate();
        _anchor.Focus();
    }

    private void CloseTimer_Tick(object? sender, EventArgs e) => BeginDismissAnimation();

    private void Notification_MouseEnter(object sender, MouseEventArgs e) => _closeTimer.Stop();

    private void Notification_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDismissing)
        {
            _closeTimer.Stop();
            _closeTimer.Start();
        }
    }

    private void Notification_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<Button>(source) is not null)
        {
            return;
        }

        RestoreAnchor();
        BeginDismissAnimation();
        e.Handled = true;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreAnchor();
        BeginDismissAnimation();
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        BeginDismissAnimation();
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _source?.RemoveHook(WindowProc);
        _source = null;
        _closeTimer.Stop();
        _closeTimer.Tick -= CloseTimer_Tick;
        SourceInitialized -= Window_SourceInitialized;
        ContentRendered -= Window_ContentRendered;
        Closed -= Window_Closed;
    }

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
}
