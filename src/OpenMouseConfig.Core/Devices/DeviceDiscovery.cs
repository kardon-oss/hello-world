using OpenMouseConfig.Core.Definitions;
using OpenMouseConfig.Core.Hid;

namespace OpenMouseConfig.Core.Devices;

/// <summary>A device definition paired with the HID collection it was found on (or none, for offline dry-run).</summary>
public sealed record DetectedDevice(DeviceDefinition Definition, DeviceMatch? Match, HidDeviceInfo? Hid)
{
    public bool IsOffline => Hid is null;

    /// <summary>Stable identity while the device stays plugged in.</summary>
    public string Key => Hid is null ? $"offline:{Definition.Id}" : $"{Definition.Id}|{Hid.Path}";

    public static DetectedDevice Offline(DeviceDefinition definition) => new(definition, null, null);
}

/// <summary>Which UI features a definition actually supports (capability declared AND command defined).</summary>
public sealed record DeviceFeatures(
    bool PollingRate,
    bool Dpi,
    bool ActiveStage,
    bool Debounce,
    bool LiftOffDistance,
    bool MotionSync,
    bool Battery,
    bool ReadBack)
{
    public static DeviceFeatures None { get; } = new(false, false, false, false, false, false, false, false);

    public static DeviceFeatures From(DeviceDefinition d)
    {
        var caps = d.Capabilities;
        return new DeviceFeatures(
            PollingRate: caps.PollingRates.Count > 0 && d.HasCommand(CommandNames.SetPollingRate),
            Dpi: caps.Dpi is not null && d.HasCommand(CommandNames.SetDpiStage),
            ActiveStage: caps.Dpi is not null && d.HasCommand(CommandNames.SetActiveDpiStage),
            Debounce: caps.Debounce is not null && d.HasCommand(CommandNames.SetDebounce),
            LiftOffDistance: caps.LiftOffDistances.Count > 0 && d.HasCommand(CommandNames.SetLiftOffDistance),
            MotionSync: caps.MotionSync && d.HasCommand(CommandNames.SetMotionSync),
            Battery: caps.Battery && d.HasCommand(CommandNames.GetBattery),
            ReadBack: d.OnConnect.Count > 0);
    }
}

/// <summary>Pairs enumerated HID collections with definitions — the native equivalent of WebHID request filters.</summary>
public static class DeviceMatcher
{
    /// <param name="definitions">Highest priority first (as returned by <see cref="DeviceCatalog"/>).</param>
    /// <param name="hidDevices">Everything the HID backend enumerated.</param>
    public static IReadOnlyList<DetectedDevice> Match(IEnumerable<DeviceDefinition> definitions, IEnumerable<HidDeviceInfo> hidDevices)
    {
        var definitionList = definitions.ToList();
        var result = new List<DetectedDevice>();

        // Each HID collection is claimed by at most one definition: the first (highest-priority) that matches.
        foreach (var hid in hidDevices.OrderBy(h => h.Path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var definition in definitionList)
            {
                var match = definition.Match.FirstOrDefault(m => m.Matches(hid));
                if (match is not null)
                {
                    result.Add(new DetectedDevice(definition, match, hid));
                    break;
                }
            }
        }

        // Keep the catalog order (user overrides first), then the match order (e.g. dongle before wired).
        return result
            .OrderBy(d => definitionList.IndexOf(d.Definition))
            .ThenBy(d => d.Definition.Match.IndexOf(d.Match!))
            .ToList();
    }
}
