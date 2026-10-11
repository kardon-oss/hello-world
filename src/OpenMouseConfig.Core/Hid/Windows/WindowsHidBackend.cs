using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenMouseConfig.Core.Hid.Windows;

/// <summary>
/// Native Windows HID enumeration: lists every HID device interface via cfgmgr32, then opens each
/// with ZERO access rights (allowed even for mice/keyboards that Windows owns exclusively) to read
/// VID/PID (HidD_GetAttributes) and usage page / report sizes (HidP_GetCaps).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHidBackend : IHidBackend
{
    public IReadOnlyList<HidDeviceInfo> Enumerate(bool includeStrings = false)
    {
        NativeMethods.HidD_GetHidGuid(out var hidGuid);

        var devices = new List<HidDeviceInfo>();
        foreach (var path in NativeMethods.GetDeviceInterfaces(hidGuid))
        {
            if (TryQuery(path, includeStrings) is { } info)
            {
                devices.Add(info);
            }
        }

        return devices;
    }

    public IHidDevice Open(HidDeviceInfo device) => HidDevice.Open(device);

    private static HidDeviceInfo? TryQuery(string path, bool includeStrings)
    {
        using var handle = NativeMethods.OpenDevice(path, access: 0, overlapped: false);
        if (handle.IsInvalid)
        {
            return null; // Device vanished or is locked down; nothing we could talk to anyway.
        }

        var attributes = new NativeMethods.HiddAttributes { Size = Marshal.SizeOf<NativeMethods.HiddAttributes>() };
        if (!NativeMethods.HidD_GetAttributes(handle, ref attributes))
        {
            return null;
        }

        var caps = default(NativeMethods.HidpCaps);
        if (NativeMethods.HidD_GetPreparsedData(handle, out var preparsed))
        {
            try
            {
                if (NativeMethods.HidP_GetCaps(preparsed, out var c) == NativeMethods.HidpStatusSuccess)
                {
                    caps = c;
                }
            }
            finally
            {
                NativeMethods.HidD_FreePreparsedData(preparsed);
            }
        }

        return new HidDeviceInfo
        {
            Path = path,
            VendorId = attributes.VendorId,
            ProductId = attributes.ProductId,
            ReleaseNumber = attributes.VersionNumber,
            UsagePage = caps.UsagePage,
            Usage = caps.Usage,
            InputReportLength = caps.InputReportByteLength,
            OutputReportLength = caps.OutputReportByteLength,
            FeatureReportLength = caps.FeatureReportByteLength,
            Manufacturer = includeStrings ? ReadString(handle, NativeMethods.HidD_GetManufacturerString) : null,
            Product = includeStrings ? ReadString(handle, NativeMethods.HidD_GetProductString) : null,
            SerialNumber = includeStrings ? ReadString(handle, NativeMethods.HidD_GetSerialNumberString) : null,
        };
    }

    private static string? ReadString(SafeFileHandle handle, Func<SafeFileHandle, byte[], int, bool> getter)
    {
        // USB string descriptors are at most 126 UTF-16 characters.
        var buffer = new byte[256];
        if (!getter(handle, buffer, buffer.Length))
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer);
        int terminator = text.IndexOf('\0');
        return (terminator >= 0 ? text[..terminator] : text).Trim();
    }
}
