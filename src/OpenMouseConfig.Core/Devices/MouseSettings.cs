using OpenMouseConfig.Core.Definitions;

namespace OpenMouseConfig.Core.Devices;

/// <summary>
/// The user-facing configuration of one mouse, independent of how it is encoded on the wire.
/// The view model edits this; <see cref="MouseController"/> turns it into packets via devices.json.
/// Plain properties so it can be persisted as JSON (last-applied values per device).
/// </summary>
public sealed class MouseSettings
{
    public int PollingRate { get; set; }

    public List<int> DpiStages { get; set; } = [];

    /// <summary>1-based.</summary>
    public int ActiveStage { get; set; } = 1;

    /// <summary>Milliseconds.</summary>
    public int Debounce { get; set; }

    /// <summary>Millimetres.</summary>
    public double LiftOffDistance { get; set; }

    public bool MotionSync { get; set; }

    /// <summary>Settings built from the definition's "defaults" block, filled up with sensible values.</summary>
    public static MouseSettings CreateDefault(DeviceDefinition definition)
    {
        var defaults = definition.Defaults;
        var settings = new MouseSettings
        {
            PollingRate = defaults.PollingRate ?? 0,
            DpiStages = defaults.DpiStages is null ? [] : [.. defaults.DpiStages],
            ActiveStage = defaults.ActiveStage,
            Debounce = defaults.Debounce ?? definition.Capabilities.Debounce?.Min ?? 0,
            LiftOffDistance = defaults.LiftOffDistance ?? 0,
            MotionSync = defaults.MotionSync,
        };
        settings.Normalize(definition);
        return settings;
    }

    public MouseSettings Clone() => new()
    {
        PollingRate = PollingRate,
        DpiStages = [.. DpiStages],
        ActiveStage = ActiveStage,
        Debounce = Debounce,
        LiftOffDistance = LiftOffDistance,
        MotionSync = MotionSync,
    };

    public int GetDpi(int stage) => stage >= 1 && stage <= DpiStages.Count ? DpiStages[stage - 1] : 0;

    /// <summary>
    /// Forces every value into what the device supports: stage count, DPI range/step, known polling
    /// rates and lift-off distances. Unsupported values fall back to a reasonable choice.
    /// </summary>
    public void Normalize(DeviceDefinition definition)
    {
        var caps = definition.Capabilities;

        if (caps.PollingRates.Count > 0 && !caps.PollingRates.Contains(PollingRate))
        {
            PollingRate = caps.PollingRates.Contains(1000) ? 1000 : caps.PollingRates[0];
        }

        DpiStages ??= [];
        if (caps.Dpi is { } dpi)
        {
            int[] fallback = [400, 800, 1600, 3200, 6400, 12800, 25600, 26000];
            while (DpiStages.Count < dpi.Stages)
            {
                DpiStages.Add(fallback[Math.Min(DpiStages.Count, fallback.Length - 1)]);
            }

            if (DpiStages.Count > dpi.Stages)
            {
                DpiStages.RemoveRange(dpi.Stages, DpiStages.Count - dpi.Stages);
            }

            for (int i = 0; i < DpiStages.Count; i++)
            {
                DpiStages[i] = dpi.Snap(DpiStages[i]);
            }

            ActiveStage = Math.Clamp(ActiveStage, 1, dpi.Stages);
        }
        else
        {
            ActiveStage = Math.Max(1, ActiveStage);
        }

        if (caps.Debounce is { } debounce)
        {
            Debounce = debounce.Clamp(Debounce);
        }

        if (caps.LiftOffDistances.Count > 0 && !caps.LiftOffDistances.Any(l => Math.Abs(l - LiftOffDistance) < 1e-6))
        {
            LiftOffDistance = caps.LiftOffDistances[0];
        }
    }

    /// <summary>All settings as variables, available to every command's fields.</summary>
    public Dictionary<string, double> ToVariables()
    {
        var vars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [VariableNames.PollingRate] = PollingRate,
            [VariableNames.ActiveStage] = ActiveStage,
            [VariableNames.StageCount] = DpiStages.Count,
            [VariableNames.Debounce] = Debounce,
            [VariableNames.LiftOffDistance] = LiftOffDistance,
            [VariableNames.MotionSync] = MotionSync ? 1 : 0,
        };

        for (int i = 0; i < DpiStages.Count; i++)
        {
            vars[VariableNames.DpiOfStage(i + 1)] = DpiStages[i];
        }

        return vars;
    }

    /// <summary>Applies values decoded from a device response (names as in <see cref="VariableNames"/>).</summary>
    /// <returns>True if anything changed.</returns>
    public bool ApplyDeviceValues(IReadOnlyDictionary<string, double> values, DeviceDefinition definition)
    {
        var before = Clone();

        if (values.TryGetValue(VariableNames.PollingRate, out var pollingRate))
        {
            PollingRate = (int)Math.Round(pollingRate);
        }

        for (int stage = 1; stage <= DpiStages.Count; stage++)
        {
            if (values.TryGetValue(VariableNames.DpiOfStage(stage), out var dpi))
            {
                DpiStages[stage - 1] = (int)Math.Round(dpi);
            }
        }

        if (values.TryGetValue(VariableNames.ActiveStage, out var active))
        {
            ActiveStage = (int)Math.Round(active);
        }

        if (values.TryGetValue(VariableNames.Debounce, out var debounce))
        {
            Debounce = (int)Math.Round(debounce);
        }

        if (values.TryGetValue(VariableNames.LiftOffDistance, out var lod))
        {
            LiftOffDistance = lod;
        }

        if (values.TryGetValue(VariableNames.MotionSync, out var motionSync))
        {
            MotionSync = motionSync >= 0.5;
        }

        Normalize(definition);
        return !SameAs(before);
    }

    private bool SameAs(MouseSettings other) =>
        PollingRate == other.PollingRate
        && DpiStages.SequenceEqual(other.DpiStages)
        && ActiveStage == other.ActiveStage
        && Debounce == other.Debounce
        && Math.Abs(LiftOffDistance - other.LiftOffDistance) < 1e-6
        && MotionSync == other.MotionSync;
}
