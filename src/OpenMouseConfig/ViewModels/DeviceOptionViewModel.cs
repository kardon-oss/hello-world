using System.IO;
using OpenMouseConfig.Core.Devices;

namespace OpenMouseConfig.ViewModels;

/// <summary>One entry of the device selector, plus the read-only facts shown in the "Device" card.</summary>
public sealed class DeviceOptionViewModel(DetectedDevice device)
{
    public DetectedDevice Device { get; } = device;

    public string Key => Device.Key;

    public bool IsOffline => Device.IsOffline;

    public bool IsVerified => Device.Definition.Verified;

    public string DisplayName => Device.Definition.Name;

    /// <summary>Second line in the dropdown.</summary>
    public string Detail => Device.Hid is { } hid
        ? $"{Device.Match?.Label ?? "USB"} · VID 0x{hid.VendorId:X4} · PID 0x{hid.ProductId:X4}"
        : "Offline definition · dry-run only";

    public string Manufacturer => Device.Definition.Manufacturer ?? "—";

    public string Identity => Device.Hid is { } hid
        ? $"VID 0x{hid.VendorId:X4} · PID 0x{hid.ProductId:X4} · rev {hid.ReleaseNumber:X4}"
        : "Not connected";

    public string Collection => Device.Hid is { } hid
        ? $"Usage page 0x{hid.UsagePage:X4} · usage 0x{hid.Usage:X4}" + (hid.InterfaceNumber is { } mi ? $" · interface {mi}" : "")
        : "—";

    public string Reports => Device.Hid is { } hid
        ? $"Feature {hid.FeatureReportLength} B · Input {hid.InputReportLength} B · Output {hid.OutputReportLength} B"
        : Device.Definition.Transport.ReportLength is { } length ? $"{length} B (transport.reportLength)" : "—";

    public string DefinitionFile => Path.GetFileName(Device.Definition.SourceFile);

    public string DefinitionPath => Device.Definition.SourceFile;

    public string VerifiedText => IsVerified ? "Verified" : "Unverified — placeholder payloads";

    public string? Notes => Device.Definition.Notes;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    public override string ToString() => DisplayName;
}
