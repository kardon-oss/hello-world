using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OpenMouseConfig.Interop;

/// <summary>Win32 bits WPF doesn't expose: dark title bar and the device-change message.</summary>
internal static class WindowInterop
{
    /// <summary>Broadcast to top-level windows whenever devices are added/removed (wParam DBT_DEVNODES_CHANGED etc.).</summary>
    public const int WmDeviceChange = 0x0219;

    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35; // Windows 11 22000+

    /// <summary>Makes the native title bar dark (Windows 10 1809+) and tints it to the header colour on Windows 11.</summary>
    public static void ApplyDarkChrome(Window window, uint captionColorRgb)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }

        // COLORREF is 0x00BBGGRR.
        int colorRef = (int)(((captionColorRgb & 0xFF) << 16) | (captionColorRgb & 0xFF00) | ((captionColorRgb >> 16) & 0xFF));
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int)); // ignored on Windows 10
    }

    /// <summary>
    /// Work area (screen minus taskbar) of the monitor the window is on, in WPF device-independent
    /// units. <see cref="SystemParameters.WorkArea"/> only describes the primary monitor.
    /// </summary>
    public static Rect? GetMonitorWorkArea(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (hwnd == IntPtr.Zero || monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        // Pixels → DIPs at the window's DPI (per-monitor aware: the DPI of that monitor).
        var fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
