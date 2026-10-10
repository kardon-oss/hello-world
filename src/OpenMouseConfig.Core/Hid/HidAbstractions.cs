using System.Text.RegularExpressions;

namespace OpenMouseConfig.Core.Hid;

/// <summary>
/// One HID top-level collection as seen by Windows. A single physical mouse usually exposes
/// several of these (mouse input, keyboard/macro, consumer control, vendor config...), each
/// with its own device path, usage page and report sizes.
/// </summary>
public sealed record HidDeviceInfo
{
    /// <summary>Device interface path, e.g. <c>\\?\hid#vid_1234&amp;pid_5678&amp;mi_02&amp;col01#...</c>.</summary>
    public required string Path { get; init; }

    public ushort VendorId { get; init; }

    public ushort ProductId { get; init; }

    /// <summary>bcdDevice (firmware/hardware revision).</summary>
    public ushort ReleaseNumber { get; init; }

    public ushort UsagePage { get; init; }

    public ushort Usage { get; init; }

    /// <summary>Byte lengths from HIDP_CAPS. They INCLUDE the report-ID byte (0 when the collection has no such report).</summary>
    public int InputReportLength { get; init; }

    public int OutputReportLength { get; init; }

    public int FeatureReportLength { get; init; }

    public string? Manufacturer { get; init; }

    public string? Product { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>USB interface number parsed from the path (<c>&amp;mi_xx</c>), if the device is composite.</summary>
    public int? InterfaceNumber => ParseInterfaceNumber(Path);

    public static int? ParseInterfaceNumber(string path)
    {
        var match = Regex.Match(path, @"[&#]mi_([0-9a-f]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? Convert.ToInt32(match.Groups[1].Value, 16) : null;
    }

    public override string ToString() =>
        $"VID 0x{VendorId:X4} PID 0x{ProductId:X4} UsagePage 0x{UsagePage:X4} Usage 0x{Usage:X4}" +
        (InterfaceNumber is { } mi ? $" MI {mi:X2}" : "") +
        $" | In {InputReportLength} Out {OutputReportLength} Feat {FeatureReportLength}" +
        (Product is { Length: > 0 } ? $" | {Manufacturer} {Product}".TrimEnd() : "");
}

/// <summary>Enumerates and opens HID devices. Implemented natively for Windows by <see cref="Windows.WindowsHidBackend"/>.</summary>
public interface IHidBackend
{
    /// <summary>Lists all present HID collections.</summary>
    /// <param name="includeStrings">
    /// Also read manufacturer/product/serial strings. Slower (each is a USB control transfer and
    /// sleeping Bluetooth devices can stall), so only used for the diagnostic listing.
    /// </param>
    IReadOnlyList<HidDeviceInfo> Enumerate(bool includeStrings = false);

    IHidDevice Open(HidDeviceInfo device);
}

/// <summary>
/// An opened HID collection. Method names mirror WebHID:
/// sendFeatureReport → <see cref="SendFeatureReport"/>, receiveFeatureReport → <see cref="ReadFeatureReport"/>,
/// sendReport → <see cref="WriteOutputReport"/>, the inputreport event → <see cref="ReadInputReport"/>.
/// <para>
/// Unlike WebHID, buffers here always START WITH THE REPORT ID (0x00 if the device doesn't use
/// report IDs) — that's the native Windows convention and also what Wireshark shows.
/// </para>
/// Calls are blocking; callers serialise access and run them off the UI thread.
/// </summary>
public interface IHidDevice : IDisposable
{
    HidDeviceInfo Info { get; }

    /// <summary>
    /// False when Windows only granted a zero-access handle (it owns mouse/keyboard collections
    /// exclusively). Feature reports still work then; input/output reports do not.
    /// </summary>
    bool CanReadWrite { get; }

    /// <summary>
    /// False once the device behind this handle is gone (unplugged, or unplugged and re-plugged:
    /// the old handle stays dead even when the device re-appears under the same path).
    /// </summary>
    bool IsConnected { get; }

    /// <summary>SET_REPORT(Feature). <paramref name="report"/>[0] is the report ID; shorter buffers are zero-padded.</summary>
    void SendFeatureReport(ReadOnlySpan<byte> report);

    /// <summary>GET_REPORT(Feature) for <paramref name="reportId"/>. The returned buffer starts with the report ID.</summary>
    byte[] ReadFeatureReport(byte reportId);

    /// <summary>Writes an output report (interrupt OUT, or SET_REPORT(Output) if the device has no OUT endpoint).</summary>
    void WriteOutputReport(ReadOnlySpan<byte> report, TimeSpan timeout);

    /// <summary>Waits for the next input report. Returns null on timeout.</summary>
    byte[]? ReadInputReport(TimeSpan timeout);

    /// <summary>Drops input reports queued by the HID driver so a following read sees fresh data.</summary>
    void FlushInputBuffer();
}

/// <summary>A HID operation failed (device unplugged, access denied, stalled request...).</summary>
public sealed class HidException : IOException
{
    public HidException(string message)
        : base(message)
    {
    }

    public HidException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
