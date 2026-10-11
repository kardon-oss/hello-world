using OpenMouseConfig.Core.Definitions;
using OpenMouseConfig.Core.Devices;
using OpenMouseConfig.Core.Diagnostics;
using OpenMouseConfig.Core.Hid;

namespace OpenMouseConfig.Core.Tests;

public class MouseControllerTests
{
    private static DeviceDefinition Definition(ResponseKind responseType = ResponseKind.Feature, string? commit = null) => new()
    {
        Id = "test",
        Name = "Test mouse",
        Transport = new TransportDefinition { ResponseType = responseType, InterCommandDelayMs = 0, ResponseTimeoutMs = 200 },
        Capabilities = new CapabilityDefinition
        {
            PollingRates = [1000, 8000],
            Dpi = new DpiCapability { Min = 50, Max = 26000, Step = 50, Stages = 4 },
            Battery = true,
        },
        CommitCommand = commit,
        OnConnect = ["getSettings", "getDpi"],
        Commands = new(StringComparer.OrdinalIgnoreCase)
        {
            [CommandNames.SetPollingRate] = new()
            {
                Payload = "05 01",
                Fields = [new() { Name = "value", Index = 2, Map = new() { ["1000"] = 1, ["8000"] = 0x40 } }],
            },
            [CommandNames.SetDpiStage] = new()
            {
                Payload = "05 02",
                Fields = [new() { Name = "stage", Index = 2, Add = -1 }, new() { Name = "dpi", Index = 3, Size = 2, Divide = 50 }],
            },
            ["save"] = new() { Payload = "05 0F" },
            [CommandNames.GetBattery] = new()
            {
                Payload = "05 10",
                Response = new() { Expect = "05 10", Fields = [new() { Name = "battery", Index = 2 }, new() { Name = "charging", Index = 3, Mask = 1 }] },
            },
            ["getSettings"] = new()
            {
                Payload = "05 11",
                Response = new() { Expect = "05 11", Fields = [new() { Name = "pollingRate", Index = 2, Map = new() { ["1000"] = 1, ["8000"] = 0x40 } }] },
            },
            ["getDpi"] = new()
            {
                Payload = "05 12",
                ForEachStage = true,
                Fields = [new() { Name = "stage", Index = 2 }],
                Response = new() { Expect = "05 12", Fields = [new() { Name = "dpi", Index = 3, Size = 2, Divide = 50 }] },
            },
        },
    };

    private static MouseSettings Settings(DeviceDefinition d) => MouseSettings.CreateDefault(d);

    [Fact]
    public async Task Set_command_sends_the_injected_feature_report_padded_to_device_length()
    {
        var device = new FakeHidDevice(featureLength: 16);
        using var controller = new MouseController(Definition(), device, NullActivityLog.Instance);
        var settings = Settings(controller.Definition);
        settings.DpiStages[2] = 1600;

        var result = await controller.SetDpiStageAsync(settings, stage: 3);

        Assert.True(result.Success);
        var sent = Assert.Single(device.SentFeatures);
        Assert.Equal(16, sent.Length);
        Assert.Equal(new byte[] { 0x05, 0x02, 0x02, 0x20, 0x00 }, sent[..5]);
    }

    [Fact]
    public async Task Dry_run_builds_and_logs_but_sends_nothing()
    {
        var device = new FakeHidDevice(featureLength: 16);
        var log = new ListLog();
        using var controller = new MouseController(Definition(), device, log) { DryRun = true };

        var result = await controller.SetPollingRateAsync(Settings(controller.Definition));

        Assert.True(result.Success);
        Assert.True(result.Simulated);
        Assert.Equal(0x01, result.Request![2]);
        Assert.Empty(device.SentFeatures);
        Assert.Contains(log.Lines, l => l.Kind == LogKind.Tx && l.Message.StartsWith("[dry-run]"));
    }

    [Fact]
    public async Task Commit_command_follows_every_successful_write()
    {
        var device = new FakeHidDevice(featureLength: 8);
        using var controller = new MouseController(Definition(commit: "save"), device, NullActivityLog.Instance);

        await controller.SetPollingRateAsync(Settings(controller.Definition));

        Assert.Equal([0x01, 0x0F], device.SentFeatures.Select(r => r[1]));
    }

    [Fact]
    public async Task Feature_response_is_polled_until_it_matches_then_decoded()
    {
        var device = new FakeHidDevice(featureLength: 8);
        device.FeatureReplies.Enqueue([0x05, 0x99, 0, 0, 0, 0, 0, 0]); // stale / unrelated
        device.FeatureReplies.Enqueue([0x05, 0x10, 87, 0x01, 0, 0, 0, 0]);
        using var controller = new MouseController(Definition(), device, NullActivityLog.Instance);

        var result = await controller.ReadBatteryAsync(Settings(controller.Definition));

        Assert.True(result.Success);
        Assert.Equal(87, result.Values["battery"]);
        Assert.Equal(1, result.Values["charging"]);
        Assert.Equal(2, device.FeatureReadCount);
    }

    [Fact]
    public async Task Input_response_skips_unrelated_reports_and_times_out_cleanly()
    {
        var device = new FakeHidDevice(featureLength: 8, inputLength: 8);
        device.InputReports.Enqueue([0x01, 0x00, 0x05]); // mouse movement
        device.InputReports.Enqueue([0x05, 0x10, 42, 0x00]);
        using var controller = new MouseController(Definition(ResponseKind.Input), device, NullActivityLog.Instance);

        var ok = await controller.ReadBatteryAsync(Settings(controller.Definition));
        Assert.True(ok.Success);
        Assert.Equal(42, ok.Values["battery"]);
        Assert.Equal(1, device.FlushCount);

        var timeout = await controller.ReadBatteryAsync(Settings(controller.Definition));
        Assert.False(timeout.Success);
        Assert.Contains("No matching input report", timeout.Error);
        Assert.Contains("within 200 ms", timeout.Error);
    }

    [Fact]
    public async Task Connect_sequence_merges_values_and_expands_per_stage_reads()
    {
        var device = new FakeHidDevice(featureLength: 8);
        device.FeatureReplies.Enqueue([0x05, 0x11, 0x40, 0, 0, 0, 0, 0]); // pollingRate 8000
        for (int stage = 1; stage <= 4; stage++)
        {
            device.FeatureReplies.Enqueue([0x05, 0x12, (byte)stage, (byte)(stage * 8), 0, 0, 0, 0]); // 400, 800, 1200, 1600
        }

        var definition = Definition();
        using var controller = new MouseController(definition, device, NullActivityLog.Instance);
        var settings = Settings(definition);

        var values = await controller.RunConnectSequenceAsync(settings);
        bool changed = settings.ApplyDeviceValues(values, definition);

        Assert.True(changed);
        Assert.Equal(8000, settings.PollingRate);
        Assert.Equal([400, 800, 1200, 1600], settings.DpiStages);
        Assert.Equal([1, 2, 3, 4], device.SentFeatures.Skip(1).Select(r => (int)r[2])); // stage injected per call
    }

    [Fact]
    public async Task Offline_controller_uses_transport_length_and_never_touches_usb()
    {
        var definition = Definition();
        definition.Transport.ReportLength = 64;
        using var controller = new MouseController(definition, device: null, NullActivityLog.Instance);

        var result = await controller.SetPollingRateAsync(Settings(definition));

        Assert.True(controller.IsSimulating);
        Assert.True(result.Simulated);
        Assert.Equal(64, result.Request!.Length);
    }

    [Fact]
    public async Task Unknown_command_and_hid_errors_become_failed_results()
    {
        var device = new FakeHidDevice(featureLength: 8) { FailSends = true };
        using var controller = new MouseController(Definition(), device, NullActivityLog.Instance);

        var unknown = await controller.ExecuteAsync("nope", Settings(controller.Definition), null);
        var failed = await controller.SetPollingRateAsync(Settings(controller.Definition));

        Assert.False(unknown.Success);
        Assert.False(failed.Success);
        Assert.Contains("unplugged", failed.Error);
    }

    [Fact]
    public async Task Feature_reply_failure_names_the_knobs_that_actually_bound_it()
    {
        var device = new FakeHidDevice(featureLength: 8); // always answers with zeros
        using var controller = new MouseController(Definition(), device, NullActivityLog.Instance);

        var result = await controller.ReadBatteryAsync(Settings(controller.Definition));

        Assert.False(result.Success);
        Assert.Contains("4 GET_FEATURE attempt(s)", result.Error); // responseRetries 3 + 1
        Assert.Contains("responseDelayMs", result.Error);
    }

    [Theory]
    [InlineData(true)]  // HidDevice.Dispose aborts the pending ReadFile (CancelIoEx) → HidException
    [InlineData(false)] // the read still runs to its timeout and returns null
    public async Task Switching_devices_during_a_pending_input_read_cancels_cleanly(bool disposeAbortsTransfer)
    {
        var log = new ListLog();
        var device = new PendingReadDevice(disposeAbortsTransfer);
        var controller = new MouseController(Definition(ResponseKind.Input), device, log);
        using var session = new CancellationTokenSource();

        var read = controller.ReadBatteryAsync(Settings(controller.Definition), session.Token);
        Assert.True(device.ReadStarted.Wait(TimeSpan.FromSeconds(5)));
        session.Cancel();      // MainViewModel.CloseSession: cancel the session token...
        controller.Dispose();  // ...then dispose the controller

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.DoesNotContain(log.Lines, l => l.Kind == LogKind.Error);
    }

    [Fact]
    public async Task Feature_reply_message_blames_the_timeout_when_the_timeout_ended_the_loop()
    {
        var definition = Definition();
        definition.Transport.ResponseDelayMs = 60;
        definition.Transport.ResponseRetries = 10;
        definition.Transport.ResponseTimeoutMs = 100;
        var device = new FakeHidDevice(featureLength: 8);
        using var controller = new MouseController(definition, device, NullActivityLog.Instance);

        var result = await controller.ReadBatteryAsync(Settings(definition));

        Assert.False(result.Success);
        Assert.Contains($"after {device.FeatureReadCount} GET_FEATURE attempt(s)", result.Error);
        Assert.Contains("timeout", result.Error);
        Assert.True(device.FeatureReadCount < 11);
    }

    [Fact]
    public async Task Disposing_between_commands_cancels_instead_of_reporting_a_device_error()
    {
        var log = new ListLog();
        MouseController? controller = null;
        var device = new FakeHidDevice(featureLength: 8)
        {
            // The UI switched devices just before this command reached the HID layer: the real
            // HidDevice then throws ObjectDisposedException when it touches the closed handle.
            OnSend = () =>
            {
                controller!.Dispose();
                throw new ObjectDisposedException("SafeFileHandle");
            },
        };
        controller = new MouseController(Definition(), device, log);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.SetPollingRateAsync(Settings(controller.Definition)));
        Assert.DoesNotContain(log.Lines, l => l.Kind == LogKind.Error);
    }

    [Fact]
    public void Device_gone_is_reported_only_for_a_live_session_whose_handle_died()
    {
        var device = new FakeHidDevice(featureLength: 8);
        var controller = new MouseController(Definition(), device, NullActivityLog.Instance);
        Assert.False(controller.IsDeviceGone);

        device.IsConnected = false;
        Assert.True(controller.IsDeviceGone);

        controller.Dispose();
        Assert.False(controller.IsDeviceGone);
        using var offline = new MouseController(Definition(), null, NullActivityLog.Instance);
        Assert.False(offline.IsDeviceGone);
    }

    private sealed class ListLog : IActivityLog
    {
        public List<(LogKind Kind, string Message)> Lines { get; } = [];

        public void Write(LogKind kind, string message)
        {
            lock (Lines)
            {
                Lines.Add((kind, message));
            }
        }
    }

    /// <summary>
    /// Models HidDevice's disposal semantics: a pending input read either gets aborted by Dispose
    /// (CancelIoEx → HidException) or keeps running until its timeout and returns null.
    /// </summary>
    private sealed class PendingReadDevice(bool disposeAbortsTransfer) : IHidDevice
    {
        private readonly ManualResetEventSlim _disposed = new();

        public ManualResetEventSlim ReadStarted { get; } = new();

        public HidDeviceInfo Info { get; } = new() { Path = "pending", FeatureReportLength = 8, InputReportLength = 8, OutputReportLength = 8 };

        public bool CanReadWrite => true;

        public bool IsConnected => true;

        public void SendFeatureReport(ReadOnlySpan<byte> report)
        {
        }

        public byte[] ReadFeatureReport(byte reportId) => new byte[8];

        public void WriteOutputReport(ReadOnlySpan<byte> report, TimeSpan timeout)
        {
        }

        public byte[]? ReadInputReport(TimeSpan timeout)
        {
            ReadStarted.Set();
            if (disposeAbortsTransfer && _disposed.Wait(timeout))
            {
                throw new HidException("ReadFile failed: The I/O operation has been aborted.");
            }

            if (!disposeAbortsTransfer)
            {
                Thread.Sleep(timeout);
            }

            return null;
        }

        public void FlushInputBuffer()
        {
        }

        public void Dispose() => _disposed.Set();
    }

    private sealed class FakeHidDevice(int featureLength, int inputLength = 0) : IHidDevice
    {
        public List<byte[]> SentFeatures { get; } = [];

        public Queue<byte[]> FeatureReplies { get; } = new();

        public Queue<byte[]> InputReports { get; } = new();

        public int FeatureReadCount { get; private set; }

        public int FlushCount { get; private set; }

        public bool FailSends { get; init; }

        public HidDeviceInfo Info { get; } = new()
        {
            Path = "fake",
            FeatureReportLength = featureLength,
            InputReportLength = inputLength,
            OutputReportLength = featureLength,
        };

        public bool CanReadWrite => true;

        public bool IsConnected { get; set; } = true;

        public Action? OnSend { get; init; }

        public void SendFeatureReport(ReadOnlySpan<byte> report)
        {
            if (FailSends)
            {
                throw new HidException("Device unplugged.");
            }

            OnSend?.Invoke();
            SentFeatures.Add(report.ToArray());
        }

        public byte[] ReadFeatureReport(byte reportId)
        {
            FeatureReadCount++;
            return FeatureReplies.Count > 0 ? FeatureReplies.Dequeue() : new byte[featureLength];
        }

        public void WriteOutputReport(ReadOnlySpan<byte> report, TimeSpan timeout) => SentFeatures.Add(report.ToArray());

        public byte[]? ReadInputReport(TimeSpan timeout) => InputReports.Count > 0 ? InputReports.Dequeue() : null;

        public void FlushInputBuffer() => FlushCount++;

        public void Dispose()
        {
        }
    }
}
