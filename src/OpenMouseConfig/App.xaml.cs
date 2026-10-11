using System.Windows;
using System.Windows.Threading;
using OpenMouseConfig.Core.Diagnostics;
using OpenMouseConfig.Core.Hid.Windows;
using OpenMouseConfig.Services;
using OpenMouseConfig.ViewModels;

namespace OpenMouseConfig;

/// <summary>
/// Composition root. The object graph is small enough that a DI container would only add noise:
/// <code>
///   MainWindow ─▶ MainViewModel ─▶ MouseController (per session) ─▶ IHidDevice (WindowsHidBackend)
///                      │                      └──▶ DeviceDefinition (devices.json)
///                      ├─▶ SettingsStore / AppPaths
///                      └─▶ UiActivityLog (developer console)
/// </code>
/// </summary>
public partial class App : Application
{
    private UiActivityLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var paths = new AppPaths();
        try
        {
            paths.EnsureUserFolders();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the bundled definitions still work.
        }

        var store = new SettingsStore(paths.SettingsFile);
        store.Load();

        _log = new UiActivityLog(Dispatcher);
        var viewModel = new MainViewModel(new WindowsHidBackend(), _log, store, paths, new WpfUserInteraction());

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        StartAsync(viewModel);
    }

    /// <summary>
    /// async void on purpose: an exception during startup is posted to the dispatcher and lands in
    /// <see cref="OnUnhandledException"/> (logged + dialog) instead of vanishing in a discarded task.
    /// </summary>
    private static async void StartAsync(MainViewModel viewModel) => await viewModel.InitializeAsync();

    /// <summary>Last line of defence: log it, tell the user, keep running.</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error($"Unexpected error: {e.Exception}");
        MessageBox.Show(
            MainWindow!,
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in the developer console.",
            "OpenMouseConfig",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
