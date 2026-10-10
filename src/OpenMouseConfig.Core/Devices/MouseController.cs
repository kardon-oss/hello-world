using System.Diagnostics;
using System.Globalization;
using OpenMouseConfig.Core.Definitions;
using OpenMouseConfig.Core.Diagnostics;
using OpenMouseConfig.Core.Hid;
using OpenMouseConfig.Core.Protocol;

namespace OpenMouseConfig.Core.Devices;

/// <summary>Outcome of one command. Failures have already been written to the activity log.</summary>
public sealed record CommandResult(
    string Command,
    bool Success,
    bool Simulated,
    byte[]? Request,
    byte[]? Response,
    IReadOnlyDictionary<string, double> Values,
    string? Error)
{
    private static readonly IReadOnlyDictionary<string, double> NoValues = new Dictionary<string, double>();

    public static CommandResult Ok(string command, byte[] request, byte[]? response, IReadOnlyDictionary<string, double>? values) =>
        new(command, true, false, request, response, values ?? NoValues, null);

    public static CommandResult DryRun(string command, byte[] request) =>
        new(command, true, true, request, null, NoValues, null);

    public static CommandResult Fail(string command, string error, byte[]? request = null) =>
        new(command, false, false, request, null, NoValues, error);
}

/// <summary>
/// Talks to one mouse using nothing but its <see cref="DeviceDefinition"/>.
/// </summary>
/// <remarks>
/// <para>
/// The typed methods (<see cref="SetPollingRateAsync"/>, <see cref="SetDpiStageAsync"/>, ...) map UI
/// actions to well-known command names; <see cref="ExecuteAsync"/> is the generic engine:
/// build the report from the template → send it as feature/output report → optionally read and
/// match the reply → decode response fields.
/// </para>
/// <para>
/// All I/O is serialised through a semaphore (one packet in flight at a time, like the vendor tools)
/// and runs on the thread pool, so callers can await from the UI thread.
/// </para>
/// <para>
/// In <see cref="DryRun"/> mode, or when no device is attached (<see cref="IsOffline"/>), every packet
/// is built and logged but nothing touches USB — the tool for checking payloads against a capture.
/// </para>
/// </remarks>
public sealed class MouseController : IDisposable
{
    private readonly IHidDevice? _device;
    private readonly IActivityLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public MouseController(DeviceDefinition definition, IHidDevice? device, IActivityLog log)
    {
        Definition = definition;
        _device = device;
        _log = log;
    }

    public DeviceDefinition Definition { get; }

    /// <summary>The opened HID collection, or null in offline mode.</summary>
    public HidDeviceInfo? DeviceInfo => _device?.Info;

    public bool IsOffline => _device is null;

    /// <summary>Log packets instead of sending them.</summary>
    public bool DryRun { get; set; }

    /// <summary>True when nothing will actually be sent (dry-run or offline).</summary>
    public bool IsSimulating => DryRun || IsOffline;

    /// <summary>
    /// True when the opened device has gone away (unplugged, or re-plugged so fast that a rescan
    /// still sees the same path). The session must then be reopened.
    /// </summary>
    public bool IsDeviceGone => !_disposed && _device is { IsConnected: false };

    // ---- Typed API used by the view model ----------------------------------------------------

    public Task<CommandResult> SetPollingRateAsync(MouseSettings settings, CancellationToken ct = default) =>
        WriteAsync(CommandNames.SetPollingRate, settings, new() { [VariableNames.Value] = settings.PollingRate }, ct);

    /// <param name="stage">1-based stage number.</param>
    public Task<CommandResult> SetDpiStageAsync(MouseSettings settings, int stage, CancellationToken ct = default)
    {
        int dpi = settings.GetDpi(stage);
        return WriteAsync(
            CommandNames.SetDpiStage,
            settings,
            new() { [VariableNames.Value] = dpi, [VariableNames.Dpi] = dpi, [VariableNames.Stage] = stage },
            ct);
    }

    public Task<CommandResult> SetActiveDpiStageAsync(MouseSettings settings, CancellationToken ct = default) =>
        WriteAsync(
            CommandNames.SetActiveDpiStage,
            settings,
            new()
            {
                [VariableNames.Value] = settings.ActiveStage,
                [VariableNames.Stage] = settings.ActiveStage,
                [VariableNames.Dpi] = settings.GetDpi(settings.ActiveStage),
            },
            ct);

    public Task<CommandResult> SetDebounceAsync(MouseSettings settings, CancellationToken ct = default) =>
        WriteAsync(CommandNames.SetDebounce, settings, new() { [VariableNames.Value] = settings.Debounce }, ct);

    public Task<CommandResult> SetLiftOffDistanceAsync(MouseSettings settings, CancellationToken ct = default) =>
        WriteAsync(CommandNames.SetLiftOffDistance, settings, new() { [VariableNames.Value] = settings.LiftOffDistance }, ct);

    public Task<CommandResult> SetMotionSyncAsync(MouseSettings settings, CancellationToken ct = default) =>
        WriteAsync(CommandNames.SetMotionSync, settings, new() { [VariableNames.Value] = settings.MotionSync ? 1 : 0 }, ct);

    public Task<CommandResult> ReadBatteryAsync(MouseSettings settings, CancellationToken ct = default) =>
        ExecuteAsync(CommandNames.GetBattery, settings, null, ct);

    /// <summary>
    /// Runs the "onConnect" commands (usually reads) and merges every decoded value.
    /// Commands flagged "forEachStage" run once per DPI stage; their "dpi" becomes "dpi{stage}".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>> RunConnectSequenceAsync(MouseSettings settings, CancellationToken ct = default)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Definition.OnConnect)
        {
            if (!Definition.TryGetCommand(name, out var command))
            {
                continue;
            }

            if (command.ForEachStage)
            {
                for (int stage = 1; stage <= settings.DpiStages.Count; stage++)
                {
                    var stageVariables = new Dictionary<string, double> { [VariableNames.Stage] = stage };
                    var result = await ExecuteAsync(name, settings, stageVariables, ct).ConfigureAwait(false);
                    foreach (var (key, value) in result.Values)
                    {
                        values[key.Equals(VariableNames.Dpi, StringComparison.OrdinalIgnoreCase) ? VariableNames.DpiOfStage(stage) : key] = value;
                    }
                }
            }
            else
            {
                var result = await ExecuteAsync(name, settings, null, ct).ConfigureAwait(false);
                foreach (var (key, value) in result.Values)
                {
                    values[key] = value;
                }
            }
        }

        return values;
    }

    // ---- Generic engine --------------------------------------------------------------------------

    /// <summary>Executes a write command, then the optional commit command (e.g. "save to flash").</summary>
    private async Task<CommandResult> WriteAsync(string name, MouseSettings settings, Dictionary<string, double> extra, CancellationToken ct)
    {
        var result = await ExecuteAsync(name, settings, extra, ct).ConfigureAwait(false);
        if (result.Success && Definition.CommitCommand is { Length: > 0 } commit
            && !commit.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            var commitResult = await ExecuteAsync(commit, settings, null, ct).ConfigureAwait(false);
            if (!commitResult.Success)
            {
                return result with { Success = false, Error = $"Setting sent, but commit command failed: {commitResult.Error}" };
            }
        }

        return result;
    }

    /// <summary>Builds, sends and (optionally) reads back one command defined in devices.json.</summary>
    /// <param name="name">Command name, e.g. "setDpiStage".</param>
    /// <param name="settings">Current settings — all of them are exposed as variables.</param>
    /// <param name="extra">Command-specific variables ("value", "stage", "dpi") that override the settings.</param>
    public async Task<CommandResult> ExecuteAsync(string name, MouseSettings settings, IReadOnlyDictionary<string, double>? extra, CancellationToken ct = default)
    {
        if (!Definition.TryGetCommand(name, out var command))
        {
            var message = $"Command \"{name}\" is not defined for {Definition.Name} in devices.json.";
            _log.Error(message);
            return CommandResult.Fail(name, message);
        }

        var variables = settings.ToVariables();
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                variables[key] = value;
            }
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => ExecuteCoreAsync(name, command, variables, ct), ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // The session was closed (device switched) while this command was queued or running.
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"{name}: {ex.Message}");
            return CommandResult.Fail(name, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CommandResult> ExecuteCoreAsync(string name, CommandDefinition command, Dictionary<string, double> variables, CancellationToken ct)
    {
        var transport = Definition.Transport;
        var kind = command.ReportType ?? transport.ReportType;
        var report = PayloadBuilder.Build(command, transport.Checksum, ResolveReportLength(kind), variables);

        if (IsSimulating)
        {
            _log.Tx($"[dry-run] {name} · {kind} · {HexFormat.FormatCompact(report)}");
            if (command.Response is not null)
            {
                _log.Info($"{name}: response not read ({(IsOffline ? "offline" : "dry-run")}).");
            }

            return CommandResult.DryRun(name, report);
        }

        var responseKind = command.Response?.Type ?? transport.ResponseType;
        if (command.Response is not null && responseKind == ResponseKind.Input)
        {
            _device!.FlushInputBuffer(); // so we don't mistake a stale report for the answer
        }

        _log.Tx($"{name} · {kind} · {HexFormat.FormatCompact(report)}");
        Send(kind, report);

        byte[]? response = null;
        IReadOnlyDictionary<string, double>? values = null;
        if (command.Response is { } responseDefinition)
        {
            response = await ReadResponseAsync(name, responseDefinition, responseKind, report, ct).ConfigureAwait(false);
            if (response is null)
            {
                var message = responseKind == ResponseKind.Feature
                    ? $"No matching response (expect \"{responseDefinition.Expect}\") after {transport.ResponseRetries + 1} GET_FEATURE attempt(s); " +
                      "increase transport.responseDelayMs or responseRetries if the device answers slowly."
                    : $"No matching input report (expect \"{responseDefinition.Expect}\") within {responseDefinition.TimeoutMs ?? transport.ResponseTimeoutMs} ms.";
                _log.Error($"{name}: {message}");
                return CommandResult.Fail(name, message, report);
            }

            values = PayloadBuilder.Decode(responseDefinition, response);
            if (values.Count > 0)
            {
                _log.Info($"{name}: " + string.Join(", ", values.Select(v => $"{v.Key}={v.Value.ToString("0.###", CultureInfo.InvariantCulture)}")));
            }
        }

        int delay = command.DelayAfterMs ?? transport.InterCommandDelayMs;
        if (delay > 0)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        return CommandResult.Ok(name, report, response, values);
    }

    /// <summary>Sends hand-typed bytes from the developer console. Respects dry-run.</summary>
    public async Task<CommandResult> SendRawAsync(byte[] data, ReportKind kind, bool readResponse, ResponseKind responseKind, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(
                async () =>
                {
                    int length = ResolveReportLength(kind);
                    if (length > 0 && data.Length > length)
                    {
                        throw new PayloadException($"{data.Length} bytes is longer than the device's {length}-byte {kind} report.");
                    }

                    var report = new byte[Math.Max(length, data.Length)];
                    data.CopyTo(report, 0);

                    if (IsSimulating)
                    {
                        _log.Tx($"[dry-run] raw · {kind} · {HexFormat.FormatCompact(report)}");
                        return CommandResult.DryRun("raw", report);
                    }

                    if (readResponse && responseKind == ResponseKind.Input)
                    {
                        _device!.FlushInputBuffer();
                    }

                    _log.Tx($"raw · {kind} · {HexFormat.FormatCompact(report)}");
                    Send(kind, report);
                    if (!readResponse)
                    {
                        return CommandResult.Ok("raw", report, null, null);
                    }

                    var timeout = TimeSpan.FromMilliseconds(Definition.Transport.ResponseTimeoutMs);
                    if (Definition.Transport.ResponseDelayMs > 0)
                    {
                        await Task.Delay(Definition.Transport.ResponseDelayMs, ct).ConfigureAwait(false);
                    }

                    byte[]? response = responseKind == ResponseKind.Feature
                        ? _device!.ReadFeatureReport(report[0])
                        : _device!.ReadInputReport(timeout);

                    if (response is null)
                    {
                        _log.Warn("raw: no input report within the timeout.");
                    }
                    else
                    {
                        _log.Rx($"raw · {responseKind} · {HexFormat.FormatCompact(response)}");
                    }

                    return CommandResult.Ok("raw", report, response, null);
                },
                ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"raw: {ex.Message}");
            return CommandResult.Fail("raw", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // The semaphore is intentionally not disposed: an in-flight command may still release it.
        _disposed = true;
        _device?.Dispose();
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>
    /// The real report size from HIDP_CAPS when a device is attached (Windows insists on it);
    /// otherwise transport.reportLength, or 0 = "use the template length".
    /// </summary>
    private int ResolveReportLength(ReportKind kind)
    {
        if (_device is null)
        {
            return Definition.Transport.ReportLength ?? 0;
        }

        int length = kind == ReportKind.Feature ? _device.Info.FeatureReportLength : _device.Info.OutputReportLength;
        if (length <= 0)
        {
            throw new HidException(
                $"The opened HID collection (usage page 0x{_device.Info.UsagePage:X4}, usage 0x{_device.Info.Usage:X4}) has no {kind} reports. " +
                "Fix usagePage/usageId in devices.json or change transport.reportType.");
        }

        return length;
    }

    private void Send(ReportKind kind, byte[] report)
    {
        if (kind == ReportKind.Feature)
        {
            _device!.SendFeatureReport(report);
        }
        else
        {
            _device!.WriteOutputReport(report, TimeSpan.FromMilliseconds(Definition.Transport.ResponseTimeoutMs));
        }
    }

    /// <summary>Reads until a report matching "expect" arrives, or the timeout expires.</summary>
    private async Task<byte[]?> ReadResponseAsync(string name, ResponseDefinition response, ResponseKind kind, byte[] request, CancellationToken ct)
    {
        var transport = Definition.Transport;
        var pattern = response.GetPattern();
        var timeout = TimeSpan.FromMilliseconds(response.TimeoutMs ?? transport.ResponseTimeoutMs);
        var clock = Stopwatch.StartNew();

        if (kind == ResponseKind.Feature)
        {
            // GET_REPORT(Feature): the device answers immediately with whatever is in its buffer,
            // so poll a few times in case the MCU hasn't processed the command yet.
            byte reportId = (byte)(response.ReportId ?? request[0]);
            for (int attempt = 0; attempt <= transport.ResponseRetries; attempt++)
            {
                await Task.Delay(Math.Max(transport.ResponseDelayMs, attempt == 0 ? 0 : 5), ct).ConfigureAwait(false);
                var data = _device!.ReadFeatureReport(reportId);
                _log.Rx($"{name} · Feature · {HexFormat.FormatCompact(data)}");
                if (pattern.Matches(data))
                {
                    return data;
                }

                if (clock.Elapsed > timeout)
                {
                    break;
                }
            }

            return null;
        }

        // Input reports: the device pushes them on its interrupt IN endpoint; skip unrelated ones.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            var data = _device!.ReadInputReport(remaining);
            if (data is null)
            {
                return null;
            }

            _log.Rx($"{name} · Input · {HexFormat.FormatCompact(data)}");
            if (pattern.Matches(data))
            {
                return data;
            }
        }
    }
}
