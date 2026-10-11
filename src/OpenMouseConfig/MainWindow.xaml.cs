using System.Windows;
using System.Windows.Interop;
using OpenMouseConfig.Interop;
using OpenMouseConfig.ViewModels;

namespace OpenMouseConfig;

/// <summary>
/// View only: everything else is bound to <see cref="MainViewModel"/>. The code-behind just bridges
/// two Win32 concerns WPF has no binding for — dark window chrome and USB plug/unplug messages.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Matches Color.Surface in Themes/Dark.xaml so the title bar blends into the header.</summary>
    private const uint CaptionColor = 0x151820;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowInterop.ApplyDarkChrome(this, CaptionColor);
        FitToMonitor();

        // Top-level windows receive WM_DEVICECHANGE (DBT_DEVNODES_CHANGED) whenever a device is
        // plugged or unplugged — enough to trigger a debounced HID rescan, no registration needed.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    /// <summary>
    /// Shrinks the startup size to the work area of the monitor the window opens on (it is not shown
    /// yet) and re-centres it there; otherwise the title bar ends up above the screen on 768-px-high
    /// laptop panels, including a laptop screen next to a larger primary monitor.
    /// </summary>
    private void FitToMonitor()
    {
        if (WindowInterop.GetMonitorWorkArea(this) is not { } work)
        {
            return;
        }

        MinHeight = Math.Min(MinHeight, work.Height);
        MinWidth = Math.Min(MinWidth, work.Width);
        if (Height > work.Height || Width > work.Width)
        {
            Height = Math.Min(Height, work.Height);
            Width = Math.Min(Width, work.Width);
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowInterop.WmDeviceChange)
        {
            ViewModel?.NotifyDevicesChanged();
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Dispose();
        base.OnClosed(e);
    }
}
