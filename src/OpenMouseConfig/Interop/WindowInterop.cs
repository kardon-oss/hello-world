using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
