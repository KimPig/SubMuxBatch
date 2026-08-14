using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SubMuxBatch.App.Services;

internal static class WindowPlacementHelper
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    public static void FitToCurrentWorkingArea(Window window, double margin = 16)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            FitToPrimaryWorkingArea(window, margin);
            return;
        }

        var dpi = GetDpiForWindow(handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var scale = dpi / 96d;
        var workWidth = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        var workHeight = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        var inset = Math.Max(0, (int)Math.Round(margin * scale));
        var availableWidth = Math.Max(1, workWidth - (inset * 2));
        var availableHeight = Math.Max(1, workHeight - (inset * 2));
        var requestedWidth = Math.Max(1, (int)Math.Round(window.Width * scale));
        var requestedHeight = Math.Max(1, (int)Math.Round(window.Height * scale));
        var targetWidth = Math.Min(requestedWidth, availableWidth);
        var targetHeight = Math.Min(requestedHeight, availableHeight);

        // WPF otherwise reapplies the design minimum after SetWindowPos and can
        // force the window beyond a genuinely smaller high-DPI work area.
        window.MinWidth = Math.Min(window.MinWidth, targetWidth / scale);
        window.MinHeight = Math.Min(window.MinHeight, targetHeight / scale);

        var left = monitorInfo.WorkArea.Left + ((workWidth - targetWidth) / 2);
        var top = monitorInfo.WorkArea.Top + ((workHeight - targetHeight) / 2);
        SetWindowPos(
            handle,
            IntPtr.Zero,
            left,
            top,
            targetWidth,
            targetHeight,
            SwpNoActivate | SwpNoZOrder);
    }

    public static void PlaceBottomRight(Window popup, Window anchor, double margin = 18)
    {
        popup.UpdateLayout();
        var popupHandle = new WindowInteropHelper(popup).Handle;
        if (popupHandle == IntPtr.Zero)
        {
            return;
        }

        var anchorHandle = new WindowInteropHelper(anchor).Handle;
        var monitorSource = anchorHandle == IntPtr.Zero ? popupHandle : anchorHandle;
        var monitor = MonitorFromWindow(monitorSource, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            PlaceBottomRightOnPrimaryWorkingArea(popup, margin);
            return;
        }

        var dpi = anchorHandle == IntPtr.Zero ? 0 : GetDpiForWindow(anchorHandle);
        if (dpi == 0)
        {
            dpi = GetDpiForWindow(popupHandle);
        }
        if (dpi == 0)
        {
            dpi = 96;
        }

        var scale = dpi / 96d;
        var inset = Math.Max(0, (int)Math.Round(margin * scale));
        var availableWidth = Math.Max(1, monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left - (inset * 2));
        var availableHeight = Math.Max(1, monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top - (inset * 2));
        var width = Math.Min(availableWidth, Math.Max(1, (int)Math.Ceiling(popup.ActualWidth * scale)));
        var height = Math.Min(availableHeight, Math.Max(1, (int)Math.Ceiling(popup.ActualHeight * scale)));
        var left = Math.Max(
            monitorInfo.WorkArea.Left + inset,
            monitorInfo.WorkArea.Right - width - inset);
        var top = Math.Max(
            monitorInfo.WorkArea.Top + inset,
            monitorInfo.WorkArea.Bottom - height - inset);
        SetWindowPos(
            popupHandle,
            IntPtr.Zero,
            left,
            top,
            width,
            height,
            SwpNoActivate | SwpNoZOrder);
    }

    private static void PlaceBottomRightOnPrimaryWorkingArea(Window popup, double margin)
    {
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (margin * 2));
        var width = Math.Min(popup.ActualWidth, availableWidth);
        var height = Math.Min(popup.ActualHeight, availableHeight);
        popup.Left = Math.Max(workArea.Left + margin, workArea.Right - width - margin);
        popup.Top = Math.Max(workArea.Top + margin, workArea.Bottom - height - margin);
    }

    private static void FitToPrimaryWorkingArea(Window window, double margin)
    {
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (margin * 2));
        var targetWidth = Math.Min(window.Width, availableWidth);
        var targetHeight = Math.Min(window.Height, availableHeight);
        window.MinWidth = Math.Min(window.MinWidth, targetWidth);
        window.MinHeight = Math.Min(window.MinHeight, targetHeight);
        window.Width = targetWidth;
        window.Height = targetHeight;
        window.Left = workArea.Left + ((workArea.Width - targetWidth) / 2);
        window.Top = workArea.Top + ((workArea.Height - targetHeight) / 2);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
