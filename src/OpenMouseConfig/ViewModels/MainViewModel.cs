using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenMouseConfig.Core.Definitions;
using OpenMouseConfig.Core.Devices;
using OpenMouseConfig.Core.Diagnostics;
using OpenMouseConfig.Core.Hid;
using OpenMouseConfig.Core.Protocol;
using OpenMouseConfig.Services;

namespace OpenMouseConfig.ViewModels;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Offline,
    Error,
}

/// <summary>
/// The dashboard. Owns the device list, the session with the selected mouse and the settings
/// shown in the UI.
/// </summary>
/// <remarks>
/// Data flow for a change:
/// <code>
///   control → bound property → On…Changed → _settings (MouseSettings) → Debouncer
///           → MouseController.Set…Async → PayloadBuilder (devices.json template) → IHidDevice
/// </code>
/// Values coming FROM the device (read-back, saved profile) are pushed into the bound properties
/// with <see cref="_applyingState"/> set, so they never echo back as writes.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SliderDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ClickDelay = TimeSpan.Zero;
    private static readonly TimeSpan RescanDelay = TimeSpan.FromMilliseconds(700);
    private static readonly string[] StageColors = ["#EF4444", "#3B82F6", "#22C55E", "#A855F7", "#F59E0B", "#06B6D4", "#EC4899", "#E5E7EB"];

    private readonly IHidBackend _hid;
    private readonly UiActivityLog _log;
    private readonly SettingsStore _store;
    private readonly AppPaths _paths;
    private readonly IUserInteraction _ui;
    private readonly Debouncer _writes = new();
    private readonly Debouncer _rescans = new();

    private DeviceCatalog _catalog = DeviceCatalog.Empty;
    private MouseController? _controller;
    private CancellationTokenSource? _sessionCts;
    private MouseSettings _settings = new();

    /// <summary>Last values the mouse actually accepted (or reported). This, not the UI state, is persisted.</summary>
    private MouseSettings _applied = new();
    private bool _applyingState;
    private bool _initialized;

    public MainViewModel(IHidBackend hid, UiActivityLog log, SettingsStore store, AppPaths paths, IUserInteraction ui)
    {
        _hid = hid;
        _log = log;
        _store = store;
        _paths = paths;
        _ui = ui;
        LogEntries = log.Entries;
    }

    // =========================================================================================
    //  Bindable state
    // =========================================================================================

    public ObservableCollection<DeviceOptionViewModel> Devices { get; } = [];

    public ObservableCollection<int> PollingRates { get; } = [];

    public ObservableCollection<DpiStageViewModel> DpiStages { get; } = [];

    public ObservableCollection<double> LiftOffDistances { get; } = [];

    public ReadOnlyObservableCollection<LogEntry> LogEntries { get; }

    public IReadOnlyList<ReportKind> RawReportKinds { get; } = [ReportKind.Feature, ReportKind.Output];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnverifiedBanner), nameof(IsSimulating), nameof(UnverifiedBannerText))]
    private DeviceOptionViewModel? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateTitle), nameof(EmptyStateText))]
    private bool _hasDevices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSimulating), nameof(ShowUnverifiedBanner), nameof(UnverifiedBannerText))]
    private bool _hasSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText), nameof(EmptyStateTitle), nameof(EmptyStateText))]
    private ConnectionState _connectionState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    private string _connectionDetail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText))]
    private int? _batteryPercent;

    [ObservableProperty]
    private bool _isCharging;

    /// <summary>Which controls the selected definition supports.</summary>
    [ObservableProperty]
    private DeviceFeatures _features = DeviceFeatures.None;

    [ObservableProperty]
    private int? _selectedPollingRate;

    [ObservableProperty]
    private string _dpiRangeText = "";

    [ObservableProperty]
    private int _debounce;

    [ObservableProperty]
    private int _debounceMin;

    [ObservableProperty]
    private int _debounceMax = 20;

    [ObservableProperty]
    private int _debounceStep = 1;

    [ObservableProperty]
    private double? _selectedLiftOffDistance;

    [ObservableProperty]
    private bool _motionSync;

    /// <summary>Build and log packets without sending them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSimulating), nameof(UnverifiedBannerText))]
    private bool _dryRun;

    /// <summary>True while the onConnect reads run; the settings cards are disabled meanwhile so edits can't race the read-back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isReadingBack;

    /// <summary>List every definition as an "offline" device so the UI and payloads can be tested without hardware.</summary>
    [ObservableProperty]
    private bool _showOfflineDevices;

    [ObservableProperty]
    private bool _isConsoleExpanded;

    [ObservableProperty]
    private string _rawHex = "";

    [ObservableProperty]
    private ReportKind _rawReportKind = ReportKind.Feature;

    [ObservableProperty]
    private bool _rawReadResponse = true;

    [ObservableProperty]
    private string _definitionsSummary = "";

    /// <summary>Feedback for the most recent write, shown in the status bar.</summary>
    [ObservableProperty]
    private string _lastAction = "";

    public string ConnectionText => ConnectionState switch
    {
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Connected => "Connected",
        ConnectionState.Offline => "Offline",
        ConnectionState.Error => "Error",
        _ => "Not connected",
    };

    public string BatteryText => BatteryPercent is { } percent ? $"{percent}%" : "—";

    /// <summary>True when packets are only logged (dry-run, or an offline definition).</summary>
    public bool IsSimulating => HasSession && (DryRun || SelectedDevice?.IsOffline == true);

    public bool ShowUnverifiedBanner => HasSession && SelectedDevice is { IsVerified: false };

    public string UnverifiedBannerText => IsSimulating
        ? "This mouse's payloads in devices.json are placeholders or unconfirmed, so nothing is sent: packets appear in the developer console instead. Compare them with your Wireshark capture, then set \"verified\": true."
        : "Dry-run is OFF: packets built from this unconfirmed definition ARE being sent to the mouse. Turn dry-run back on if anything looks wrong.";

    public bool CanEdit => !IsReadingBack;

    public string EmptyStateTitle => ConnectionState switch
    {
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Error => "Couldn't open the mouse",
        _ => HasDevices ? "Select a mouse" : "No supported mouse found",
    };

    public string EmptyStateText => ConnectionState == ConnectionState.Error
        ? ConnectionDetail
        : "Plug in a mouse listed in devices.json (wired or via its receiver). To explore the UI or check payloads " +
          "without hardware, show the offline definitions — everything then runs in dry-run mode.";

    // =========================================================================================
    //  Startup / device discovery
    // =========================================================================================

    public async Task InitializeAsync()
    {
        ShowOfflineDevices = _store.Settings.ShowOfflineDevices;
        IsConsoleExpanded = _store.Settings.ConsoleExpanded;
        _log.Info($"Definitions: {_paths.BundledDefinitionsFile} + {_paths.UserDefinitionsDirectory}\\*.json");
        try
        {
            LoadCatalog();
        }
        finally
        {
            _initialized = true; // even after a failed load, plug events and Reload must keep working
        }

        await RefreshDevicesAsync();
    }

    /// <summary>Called by the window on WM_DEVICECHANGE (USB plug/unplug). Debounced: Windows sends bursts.</summary>
    public void NotifyDevicesChanged() => _rescans.Run("rescan", RescanDelay, RefreshDevicesAsync);

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        IReadOnlyList<HidDeviceInfo> hids;
        try
        {
            hids = await Task.Run(() => _hid.Enumerate());
        }
        catch (Exception ex)
        {
            _log.Error($"HID enumeration failed: {ex.Message}");
            hids = [];
        }

        var detected = DeviceMatcher.Match(_catalog.Devices, hids).ToList();
        if (ShowOfflineDevices)
        {
            detected.AddRange(_catalog.Devices.Select(DetectedDevice.Offline));
        }

        SyncDeviceList(detected);
    }

    /// <summary>Updates the dropdown in place so the current selection (and session) survives unrelated plug events.</summary>
    private void SyncDeviceList(IReadOnlyList<DetectedDevice> detected)
    {
        var keys = detected.Select(d => d.Key).ToHashSet();
        foreach (var gone in Devices.Where(d => !keys.Contains(d.Key)).ToList())
        {
            if (!gone.IsOffline)
            {
                _log.Info($"Disconnected: {gone.DisplayName}");
            }

            Devices.Remove(gone);
        }

        for (int i = 0; i < detected.Count; i++)
        {
            if (Devices.Any(d => d.Key == detected[i].Key))
            {
                continue;
            }

            var option = new DeviceOptionViewModel(detected[i]);
            Devices.Insert(Math.Min(i, Devices.Count), option);
            if (!option.IsOffline)
            {
                _log.Info($"Found: {option.DisplayName} ({option.Detail})");
            }
        }

        HasDevices = Devices.Count > 0;
        if (SelectedDevice is null || !Devices.Contains(SelectedDevice))
        {
            var lastId = _store.Settings.LastDefinitionId;
            SelectedDevice =
                Devices.FirstOrDefault(d => !d.IsOffline && d.Device.Definition.Id == lastId)
                ?? Devices.FirstOrDefault(d => !d.IsOffline)
                ?? Devices.FirstOrDefault(d => d.Device.Definition.Id == lastId)
                ?? Devices.FirstOrDefault();
        }
        else if (ConnectionState == ConnectionState.Error)
        {
            _ = ConnectAsync(SelectedDevice); // Rescan/F5 or a plug event retries a failed open
        }
        else if (_controller is { IsDeviceGone: true })
        {
            // Unplugged and re-plugged within the rescan delay: same path, but our handle is dead.
            _log.Warn($"{SelectedDevice.DisplayName} was reconnected; reopening it.");
            _ = ConnectAsync(SelectedDevice);
        }
    }

    private void LoadCatalog()
    {
        try
        {
            _catalog = DeviceCatalog.Load(_paths.GetDefinitionFiles());
        }
        catch (Exception ex)
        {
            _catalog = DeviceCatalog.Empty;
            _log.Error($"Loading device definitions failed: {ex.Message}");
            DefinitionsSummary = "Loading device definitions failed, see console";
            IsConsoleExpanded = true;
            return;
        }

        foreach (var message in _catalog.Messages)
        {
            var kind = message.Severity switch
            {
                CatalogSeverity.Error => LogKind.Error,
                CatalogSeverity.Warning => LogKind.Warning,
                _ => LogKind.Info,
            };
            _log.Write(kind, message.ToString());
        }

        int errors = _catalog.ErrorCount;
        DefinitionsSummary = $"{_catalog.Devices.Count} device definition(s) from {_catalog.Files.Count} file(s)"
            + (errors > 0 ? $" · {errors} error(s), see console" : "");
        _log.Info($"Loaded {DefinitionsSummary}.");

        if (_catalog.Files.Count == 0)
        {
            _log.Error($"No definition files found. Expected {_paths.BundledDefinitionsFile}.");
        }

        if (errors > 0)
        {
            IsConsoleExpanded = true;
        }
    }

    // =========================================================================================
    //  Session lifecycle
    // =========================================================================================

    partial void OnSelectedDeviceChanged(DeviceOptionViewModel? value)
    {
        if (value is not null)
        {
            _store.Settings.LastDefinitionId = value.Device.Definition.Id;
        }

        _ = ConnectAsync(value);
    }

    private async Task ConnectAsync(DeviceOptionViewModel? option)
    {
        CloseSession();
        if (option is null)
        {
            ConnectionState = ConnectionState.Disconnected;
            ConnectionDetail = "";
            return;
        }

        var session = new CancellationTokenSource();
        _sessionCts = session;
        var token = session.Token; // captured now: CloseSession() may dispose the source while we await
        var definition = option.Device.Definition;
        ConnectionState = ConnectionState.Connecting;
        ConnectionDetail = definition.Name;

        IHidDevice? hid = null;
        if (option.Device.Hid is { } info)
        {
            try
            {
                hid = await Task.Run(() => _hid.Open(info));
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    ConnectionState = ConnectionState.Error;
                    ConnectionDetail = ex.Message;
                    _log.Error($"Could not open {definition.Name}: {ex.Message}");
                }

                return;
            }
        }

        if (token.IsCancellationRequested)
        {
            hid?.Dispose(); // the user picked another device while this one was opening
            return;
        }

        try
        {
            await StartSessionAsync(option, definition, hid, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"Connecting to {definition.Name} failed: {ex.Message}");
            CloseSession();
            ConnectionState = ConnectionState.Error;
            ConnectionDetail = ex.Message;
        }
    }

    private async Task StartSessionAsync(DeviceOptionViewModel option, DeviceDefinition definition, IHidDevice? hid, CancellationToken token)
    {
        var controller = new MouseController(definition, hid, _log);
        _controller = controller;

        Features = DeviceFeatures.From(definition);
        ConfigureControls(definition);
        _settings = _store.GetProfile(definition.Id) ?? MouseSettings.CreateDefault(definition);
        _settings.Normalize(definition);
        _applied = _settings.Clone();
        PushSettingsToUi();
        HasSession = true;

        if (!definition.Verified && !DryRun)
        {
            _log.Warn($"{definition.Name} is \"verified\": false in {option.DefinitionFile} — dry-run enabled automatically.");
            DryRun = true;
        }

        controller.DryRun = DryRun;
        ConnectionState = controller.IsOffline ? ConnectionState.Offline : ConnectionState.Connected;
        ConnectionDetail = controller.IsOffline ? $"{definition.Name} (offline definition)" : $"{definition.Name} · {option.Detail}";

        if (hid is not null)
        {
            _log.Success($"Opened {definition.Name}: {hid.Info.Path}");
            if (!hid.CanReadWrite)
            {
                _log.Warn("Windows granted a zero-access handle (system-owned collection): only feature reports will work.");
            }
        }
        else
        {
            _log.Info($"{definition.Name}: offline definition selected, packets are logged only.");
        }

        await ReadFromDeviceCoreAsync(controller, token);
        if (!token.IsCancellationRequested)
        {
            _ = BatteryLoopAsync(controller, token);
        }
    }

    private void CloseSession()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        _writes.CancelAll();

        if (_controller is not null)
        {
            _controller.Dispose();
            _controller = null;
            _store.Save();
        }

        HasSession = false;
        IsReadingBack = false;
        Features = DeviceFeatures.None;
        BatteryPercent = null;
        IsCharging = false;
    }

    /// <summary>Rebuilds dropdown/slider ranges for the selected definition.</summary>
    private void ConfigureControls(DeviceDefinition definition)
    {
        var caps = definition.Capabilities;

        _applyingState = true;
        try
        {
            PollingRates.Clear();
            foreach (var rate in caps.PollingRates)
            {
                PollingRates.Add(rate);
            }

            DpiStages.Clear();
            if (caps.Dpi is { } dpi)
            {
                for (int stage = 1; stage <= dpi.Stages; stage++)
                {
                    DpiStages.Add(new DpiStageViewModel(stage, StageColors[(stage - 1) % StageColors.Length], dpi, OnStageDpiChanged, OnStageActivated));
                }

                DpiRangeText = string.Format(CultureInfo.CurrentCulture, "{0:N0} – {1:N0} DPI in steps of {2}", dpi.Min, dpi.Max, dpi.Step);
            }
            else
            {
                DpiRangeText = "Not supported by this definition.";
            }

            DebounceMin = caps.Debounce?.Min ?? 0;
            DebounceMax = caps.Debounce?.Max ?? 20;
            DebounceStep = caps.Debounce?.Step ?? 1;

            LiftOffDistances.Clear();
            foreach (var distance in caps.LiftOffDistances)
            {
                LiftOffDistances.Add(distance);
            }
        }
        finally
        {
            _applyingState = false;
        }
    }

    /// <summary>Copies <see cref="_settings"/> into the bound properties without triggering writes.</summary>
    private void PushSettingsToUi()
    {
        _applyingState = true;
        try
        {
            SelectedPollingRate = PollingRates.Contains(_settings.PollingRate) ? _settings.PollingRate : null;
            foreach (var stage in DpiStages)
            {
                stage.Load(_settings.GetDpi(stage.Number), stage.Number == _settings.ActiveStage);
            }

            Debounce = _settings.Debounce;
            SelectedLiftOffDistance = LiftOffDistances.Cast<double?>().FirstOrDefault(d => Math.Abs(d!.Value - _settings.LiftOffDistance) < 1e-6);
            MotionSync = _settings.MotionSync;
        }
        finally
        {
            _applyingState = false;
        }
    }

    // =========================================================================================
    //  UI → device
    // =========================================================================================

    partial void OnSelectedPollingRateChanged(int? value)
    {
        if (_applyingState || value is null)
        {
            return;
        }

        _settings.PollingRate = value.Value;
        QueueWrite(CommandNames.SetPollingRate, "pollingRate", ClickDelay, $"Polling rate {value} Hz", (c, s, ct) => c.SetPollingRateAsync(s, ct),
            (sent, applied) => applied.PollingRate = sent.PollingRate);
    }

    private void OnStageDpiChanged(DpiStageViewModel stage)
    {
        if (_applyingState || stage.Number > _settings.DpiStages.Count)
        {
            return;
        }

        _settings.DpiStages[stage.Number - 1] = stage.Dpi;
        QueueWrite(CommandNames.SetDpiStage, $"dpi{stage.Number}", SliderDelay, $"DPI stage {stage.Number} → {stage.Dpi:N0}", (c, s, ct) => c.SetDpiStageAsync(s, stage.Number, ct),
            (sent, applied) => applied.DpiStages[stage.Number - 1] = sent.DpiStages[stage.Number - 1]);
    }

    private void OnStageActivated(DpiStageViewModel stage)
    {
        foreach (var other in DpiStages.Where(s => s != stage))
        {
            other.Deactivate();
        }

        if (_applyingState)
        {
            return;
        }

        _settings.ActiveStage = stage.Number;
        if (Features.ActiveStage)
        {
            QueueWrite(CommandNames.SetActiveDpiStage, "activeStage", ClickDelay, $"Active DPI stage {stage.Number}", (c, s, ct) => c.SetActiveDpiStageAsync(s, ct),
                (sent, applied) => applied.ActiveStage = sent.ActiveStage);
        }
    }

    partial void OnDebounceChanged(int value)
    {
        if (_applyingState)
        {
            return;
        }

        _settings.Debounce = value;
        QueueWrite(CommandNames.SetDebounce, "debounce", SliderDelay, $"Debounce {value} ms", (c, s, ct) => c.SetDebounceAsync(s, ct),
            (sent, applied) => applied.Debounce = sent.Debounce);
    }

    partial void OnSelectedLiftOffDistanceChanged(double? value)
    {
        if (_applyingState || value is null)
        {
            return;
        }

        _settings.LiftOffDistance = value.Value;
        QueueWrite(CommandNames.SetLiftOffDistance, "lod", ClickDelay, $"Lift-off distance {value:0.#} mm", (c, s, ct) => c.SetLiftOffDistanceAsync(s, ct),
            (sent, applied) => applied.LiftOffDistance = sent.LiftOffDistance);
    }

    partial void OnMotionSyncChanged(bool value)
    {
        if (_applyingState)
        {
            return;
        }

        _settings.MotionSync = value;
        QueueWrite(CommandNames.SetMotionSync, "motionSync", ClickDelay, $"Motion Sync {(value ? "on" : "off")}", (c, s, ct) => c.SetMotionSyncAsync(s, ct),
            (sent, applied) => applied.MotionSync = sent.MotionSync);
    }

    /// <summary>
    /// Debounces and sends one setting. A snapshot of the settings is taken when the write fires,
    /// so the background I/O never sees a half-edited object. On real success, <paramref name="commit"/>
    /// copies the sent value into <see cref="_applied"/>, which is what gets persisted, together with any
    /// other setting the <paramref name="command"/>'s packet carried.
    /// </summary>
    private void QueueWrite(
        string command,
        string key,
        TimeSpan delay,
        string label,
        Func<MouseController, MouseSettings, CancellationToken, Task<CommandResult>> write,
        Action<MouseSettings, MouseSettings> commit)
    {
        if (_controller is not { } controller || _sessionCts is not { } session || IsReadingBack)
        {
            return;
        }

        var token = session.Token;
        _writes.Run(key, delay, async () =>
        {
            if (_controller != controller)
            {
                return; // device changed while we were waiting
            }

            try
            {
                var sent = _settings.Clone();
                var result = await write(controller, sent, token);
                if (_controller != controller)
                {
                    return;
                }

                if (!result.Success)
                {
                    LastAction = $"{label} — failed: {result.Error}";
                }
                else if (result.Simulated)
                {
                    LastAction = $"{label} — logged (dry-run)";
                }
                else
                {
                    LastAction = $"{label} — applied";
                    commit(sent, _applied);

                    // A packet can carry more than the edited setting (a "full config" command, or a
                    // commit command with fields): everything it carried is now on the mouse.
                    _applied.CopyFrom(sent, controller.Definition.VariablesWrittenBy(command));
                    _store.SetProfile(controller.Definition.Id, _applied);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    partial void OnDryRunChanged(bool value)
    {
        if (!value && SelectedDevice is { IsVerified: false, IsOffline: false } device)
        {
            bool confirmed = _ui.Confirm(
                "Send unverified packets?",
                $"The definition for {device.DisplayName} is marked \"verified\": false — its payloads may still be placeholders.\n\n" +
                "Sending wrong bytes can change settings unexpectedly or, in the worst case, upset the mouse firmware.\n\n" +
                "Only continue if you've compared the logged packets with your own capture. Send real packets now?");

            if (!confirmed)
            {
                DryRun = true; // re-entrant set; the binding re-reads the value
                return;
            }

            if (DryRun != value)
            {
                return; // a session started while the dialog was open already forced dry-run back on
            }
        }

        if (_controller is not null)
        {
            _controller.DryRun = value;
        }

        if (_initialized && HasSession)
        {
            if (value)
            {
                _log.Warn("Dry-run ON: packets are logged here but not sent.");
            }
            else
            {
                _log.Warn("Dry-run OFF: packets are sent to the device. Use \"Read from mouse\" or \"Write all settings\" to sync.");
            }
        }
    }

    partial void OnShowOfflineDevicesChanged(bool value)
    {
        _store.Settings.ShowOfflineDevices = value;
        if (_initialized)
        {
            _ = RefreshDevicesAsync();
        }
    }

    partial void OnIsConsoleExpandedChanged(bool value) => _store.Settings.ConsoleExpanded = value;

    // =========================================================================================
    //  Device → UI
    // =========================================================================================

    [RelayCommand]
    private Task ReadFromDeviceAsync() =>
        _controller is { } controller && _sessionCts is { } session
            ? ReadFromDeviceCoreAsync(controller, session.Token, userRequested: true)
            : Task.CompletedTask;

    /// <summary>Runs the "onConnect" reads and applies the decoded values to the UI.</summary>
    private async Task ReadFromDeviceCoreAsync(MouseController controller, CancellationToken ct, bool userRequested = false)
    {
        if (controller.Definition.OnConnect.Count == 0)
        {
            if (userRequested)
            {
                _log.Info("This definition has no \"onConnect\" read commands.");
            }

            return;
        }

        if (controller.IsSimulating)
        {
            _log.Info("Read-back skipped in dry-run/offline mode (reading also sends request packets). Showing saved/default values.");
            return;
        }

        // A write queued just before (e.g. typing a DPI value, then clicking "Read from mouse") must hit
        // the mouse before we read, or the read would revert the UI while the write still goes out.
        await _writes.FlushAsync();
        if (_controller != controller)
        {
            return;
        }

        IsReadingBack = true;
        try
        {
            var values = await controller.RunConnectSequenceAsync(_settings.Clone(), ct);
            if (_controller != controller || values.Count == 0)
            {
                return;
            }

            ApplyBattery(values);
            if (_settings.ApplyDeviceValues(values, controller.Definition))
            {
                PushSettingsToUi();
                _log.Info("UI updated with the mouse's current configuration.");
            }

            // Only what the mouse reported is known to be applied; other settings keep their last known value.
            _applied.ApplyDeviceValues(values, controller.Definition);
            _store.SetProfile(controller.Definition.Id, _applied);
            LastAction = "Settings read from the mouse";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_controller == controller)
            {
                IsReadingBack = false;
            }
        }
    }

    private async Task BatteryLoopAsync(MouseController controller, CancellationToken ct)
    {
        if (!Features.Battery)
        {
            return;
        }

        bool readOnConnect = controller.Definition.OnConnect.Contains(CommandNames.GetBattery, StringComparer.OrdinalIgnoreCase);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(controller.Definition.BatteryPollSeconds));
        try
        {
            if (!readOnConnect)
            {
                await ReadBatteryOnceAsync(controller, ct);
            }

            while (await timer.WaitForNextTickAsync(ct))
            {
                await ReadBatteryOnceAsync(controller, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadBatteryOnceAsync(MouseController controller, CancellationToken ct)
    {
        if (controller.IsSimulating || _controller != controller)
        {
            return;
        }

        var result = await controller.ReadBatteryAsync(_settings.Clone(), ct);
        if (result.Success && _controller == controller)
        {
            ApplyBattery(result.Values);
        }
    }

    private void ApplyBattery(IReadOnlyDictionary<string, double> values)
    {
        if (values.TryGetValue(VariableNames.Battery, out var battery))
        {
            BatteryPercent = (int)Math.Clamp(Math.Round(battery), 0, 100);
        }

        if (values.TryGetValue(VariableNames.Charging, out var charging))
        {
            IsCharging = charging >= 0.5;
        }
    }

    // =========================================================================================
    //  Other commands
    // =========================================================================================

    /// <summary>Sends every supported setting (useful after leaving dry-run, or to restore a profile).</summary>
    [RelayCommand]
    private async Task WriteAllAsync()
    {
        if (_controller is not { } c || _sessionCts is not { } session || IsReadingBack)
        {
            return;
        }

        var s = _settings.Clone();
        var ct = session.Token;
        var results = new List<CommandResult>();
        try
        {
            if (Features.PollingRate)
            {
                results.Add(await c.SetPollingRateAsync(s, ct));
            }

            if (Features.Dpi)
            {
                for (int stage = 1; stage <= s.DpiStages.Count; stage++)
                {
                    results.Add(await c.SetDpiStageAsync(s, stage, ct));
                }
            }

            if (Features.ActiveStage)
            {
                results.Add(await c.SetActiveDpiStageAsync(s, ct));
            }

            if (Features.Debounce)
            {
                results.Add(await c.SetDebounceAsync(s, ct));
            }

            if (Features.LiftOffDistance)
            {
                results.Add(await c.SetLiftOffDistanceAsync(s, ct));
            }

            if (Features.MotionSync)
            {
                results.Add(await c.SetMotionSyncAsync(s, ct));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        int failed = results.Count(r => !r.Success);
        LastAction = failed > 0
            ? $"Write all — {failed} of {results.Count} command(s) failed"
            : $"Write all — {results.Count} command(s) {(c.IsSimulating ? "logged (dry-run)" : "applied")}";
        if (failed == 0 && !c.IsSimulating && _controller == c)
        {
            _applied = s.Clone();
            _store.SetProfile(c.Definition.Id, _applied);
        }
    }

    [RelayCommand]
    private async Task ReloadDefinitionsAsync()
    {
        _log.Info("Reloading device definitions…");
        SelectedDevice = null; // closes the session, so changed definitions take effect on reconnect
        Devices.Clear();
        LoadCatalog();
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    private void OpenDefinitionsFolder() => _ui.OpenFolder(_paths.UserDefinitionsDirectory);

    [RelayCommand]
    private void EnableOfflineDevices() => ShowOfflineDevices = true;

    /// <summary>Dumps every HID collection — the quickest way to find VID/PID/usage page for a new mouse.</summary>
    [RelayCommand]
    private async Task ListHidInterfacesAsync()
    {
        IsConsoleExpanded = true;
        try
        {
            var all = await Task.Run(() => _hid.Enumerate(includeStrings: true));
            _log.Info($"{all.Count} HID collection(s) present:");
            foreach (var device in all.OrderBy(d => d.VendorId).ThenBy(d => d.ProductId).ThenBy(d => d.UsagePage))
            {
                _log.Info("  " + device);
            }

            _log.Info("Your mouse's config interface is usually the one with a vendor-defined usage page (0xFF00–0xFFFF) and a non-zero Feat or Out length.");
        }
        catch (Exception ex)
        {
            _log.Error($"HID enumeration failed: {ex.Message}");
        }
    }

    /// <summary>Sends hand-typed hex from the console (byte 0 = report ID).</summary>
    [RelayCommand]
    private async Task SendRawAsync()
    {
        if (_controller is not { } controller || _sessionCts is not { } session)
        {
            _log.Warn("Select a device first (or enable \"Show offline definitions\" to try without one).");
            return;
        }

        byte[] data;
        try
        {
            data = HexFormat.Parse(RawHex);
        }
        catch (FormatException ex)
        {
            _log.Error($"raw: {ex.Message}");
            return;
        }

        if (data.Length == 0)
        {
            return;
        }

        try
        {
            await controller.SendRawAsync(data, RawReportKind, RawReadResponse, controller.Definition.Transport.ResponseType, session.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private void CopyLog() => _ui.CopyToClipboard(string.Join(Environment.NewLine, LogEntries));

    [RelayCommand]
    private void ClearLog() => _log.Clear();

    public void Dispose()
    {
        _rescans.CancelAll();
        CloseSession();
        _store.Save();
    }
}
