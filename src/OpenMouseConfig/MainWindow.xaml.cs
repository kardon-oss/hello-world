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

        // Top-level windows receive WM_DEVICECHANGE (DBT_DEVNODES_CHANGED) whenever a device is
        // plugged or unplugged — enough to trigger a debounced HID rescan, no registration needed.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
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
