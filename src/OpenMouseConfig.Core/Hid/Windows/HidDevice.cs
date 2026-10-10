using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenMouseConfig.Core.Hid.Windows;

/// <summary>
/// An opened Windows HID collection — the native counterpart of a WebHID <c>HIDDevice</c>.
/// </summary>
/// <remarks>
/// <para><b>Feature reports</b> use HidD_SetFeature / HidD_GetFeature (control transfers on EP0).</para>
/// <para><b>Input/output reports</b> use overlapped ReadFile / WriteFile so we can apply timeouts
/// and cancel cleanly (CancelIoEx) instead of blocking forever on a silent device.</para>
/// <para>All buffers start with the report ID and have the exact length from HIDP_CAPS, which
/// Windows requires (it rejects shorter buffers with ERROR_INVALID_PARAMETER).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HidDevice : IHidDevice
{
    private readonly SafeFileHandle _handle;

    private HidDevice(HidDeviceInfo info, SafeFileHandle handle, bool canReadWrite)
    {
        Info = info;
        _handle = handle;
        CanReadWrite = canReadWrite;
    }

    public HidDeviceInfo Info { get; }

    public bool CanReadWrite { get; }

    /// <summary>Opens the device, falling back to a zero-access handle if read/write is denied.</summary>
    public static HidDevice Open(HidDeviceInfo info)
    {
        var handle = NativeMethods.OpenDevice(info.Path, NativeMethods.GenericRead | NativeMethods.GenericWrite, overlapped: true);
        if (!handle.IsInvalid)
        {
            return new HidDevice(info, handle, canReadWrite: true);
        }

        int readWriteError = Marshal.GetLastPInvokeError();
        handle.Dispose();

        // Windows opens mouse and keyboard top-level collections exclusively (anti key-logger), which
        // shows up as ERROR_ACCESS_DENIED. Feature reports still work on a handle opened with no
        // access rights — the same trick hidapi and Chromium use. Any other error, e.g.
        // ERROR_SHARING_VIOLATION from vendor software holding the device, is reported as-is.
        if (readWriteError == NativeMethods.ErrorAccessDenied)
        {
            handle = NativeMethods.OpenDevice(info.Path, access: 0, overlapped: true);
            if (!handle.IsInvalid)
            {
                return new HidDevice(info, handle, canReadWrite: false);
            }

            handle.Dispose();
        }

        throw new HidException(
            $"Could not open HID device ({new Win32Exception(readWriteError).Message}). " +
            "Is another program (e.g. the vendor's configuration software) holding it exclusively?",
            new Win32Exception(readWriteError));
    }

    public void SendFeatureReport(ReadOnlySpan<byte> report)
    {
        var buffer = ToReportBuffer(report, Info.FeatureReportLength, "feature");
        if (!NativeMethods.HidD_SetFeature(_handle, buffer, buffer.Length))
        {
            throw LastError("HidD_SetFeature");
        }
    }

    public byte[] ReadFeatureReport(byte reportId)
    {
        RequireLength(Info.FeatureReportLength, "feature");
        var buffer = new byte[Info.FeatureReportLength];
        buffer[0] = reportId;
        if (!NativeMethods.HidD_GetFeature(_handle, buffer, buffer.Length))
        {
            throw LastError("HidD_GetFeature");
        }

        return buffer;
    }

    public unsafe void WriteOutputReport(ReadOnlySpan<byte> report, TimeSpan timeout)
    {
        RequireReadWrite("write output reports");
        var buffer = ToReportBuffer(report, Info.OutputReportLength, "output");
        fixed (byte* p = buffer)
        {
            int written = OverlappedTransfer(write: true, p, buffer.Length, timeout);
            if (written < 0)
            {
                throw new HidException($"Timed out after {timeout.TotalMilliseconds:0} ms writing an output report.");
            }
        }
    }

    public unsafe byte[]? ReadInputReport(TimeSpan timeout)
    {
        RequireReadWrite("read input reports");
        RequireLength(Info.InputReportLength, "input");
        var buffer = new byte[Info.InputReportLength];
        fixed (byte* p = buffer)
        {
            int read = OverlappedTransfer(write: false, p, buffer.Length, timeout);
            return read < 0 ? null : buffer[..read];
        }
    }

    public void FlushInputBuffer()
    {
        // Best effort: fails harmlessly on zero-access handles.
        NativeMethods.HidD_FlushQueue(_handle);
    }

    public bool IsConnected
    {
        get
        {
            // After a surprise removal the handle stays open but every IOCTL on it fails, even if a
            // re-plugged device re-appears under the same path.
            var attributes = new NativeMethods.HiddAttributes { Size = Marshal.SizeOf<NativeMethods.HiddAttributes>() };
            try
            {
                return !_handle.IsClosed && NativeMethods.HidD_GetAttributes(_handle, ref attributes);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// Runs one overlapped ReadFile/WriteFile and waits up to <paramref name="timeout"/>.
    /// Returns the byte count, or -1 on timeout (the I/O is cancelled and fully completed before
    /// returning, so the caller's pinned buffer can be released safely).
    /// </summary>
    private unsafe int OverlappedTransfer(bool write, byte* buffer, int length, TimeSpan timeout)
    {
        bool addedRef = false;
        NativeOverlapped* overlapped = null;
        try
        {
            // Hold a reference on the handle for the whole transfer. A concurrent Dispose (the user
            // switched devices) then only marks it, and CloseHandle runs after the kernel is done
            // with the OVERLAPPED and the pinned buffer, never while the I/O may still be pending.
            _handle.DangerousAddRef(ref addedRef);

            using var completed = new ManualResetEvent(initialState: false);
            overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            overlapped->EventHandle = completed.SafeWaitHandle.DangerousGetHandle();

            bool finishedSynchronously = write
                ? NativeMethods.WriteFile(_handle, buffer, length, IntPtr.Zero, overlapped)
                : NativeMethods.ReadFile(_handle, buffer, length, IntPtr.Zero, overlapped);

            if (!finishedSynchronously)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != NativeMethods.ErrorIoPending)
                {
                    throw new HidException($"{(write ? "WriteFile" : "ReadFile")} failed: {new Win32Exception(error).Message}", new Win32Exception(error));
                }

                if (!completed.WaitOne(timeout))
                {
                    NativeMethods.CancelIoEx(_handle, overlapped);

                    // Wait for the cancellation to land; the kernel still owns `buffer`/`overlapped` until
                    // then. The transfer may also have completed just before the cancel: keep its result.
                    if (NativeMethods.GetOverlappedResult(_handle, overlapped, out int lateTransfer, wait: true))
                    {
                        return lateTransfer;
                    }

                    if (Marshal.GetLastPInvokeError() == NativeMethods.ErrorOperationAborted)
                    {
                        return -1;
                    }

                    throw LastError(write ? "WriteFile" : "ReadFile");
                }
            }

            if (!NativeMethods.GetOverlappedResult(_handle, overlapped, out int transferred, wait: false))
            {
                throw LastError(write ? "WriteFile" : "ReadFile");
            }

            return transferred;
        }
        finally
        {
            if (overlapped != null)
            {
                NativeMemory.Free(overlapped);
            }

            if (addedRef)
            {
                _handle.DangerousRelease();
            }
        }
    }

    /// <summary>Copies the report into a buffer of exactly the size Windows expects, zero-padding the tail.</summary>
    private static byte[] ToReportBuffer(ReadOnlySpan<byte> report, int requiredLength, string kind)
    {
        RequireLength(requiredLength, kind);
        if (report.Length > requiredLength)
        {
            throw new HidException($"{report.Length}-byte {kind} report is longer than the device's {requiredLength}-byte report.");
        }

        var buffer = new byte[requiredLength];
        report.CopyTo(buffer);
        return buffer;
    }

    private static void RequireLength(int length, string kind)
    {
        if (length <= 0)
        {
            throw new HidException(
                $"This HID collection has no {kind} reports. The usagePage/usageId in devices.json probably selects the wrong collection.");
        }
    }

    private void RequireReadWrite(string action)
    {
        if (!CanReadWrite)
        {
            throw new HidException(
                $"Cannot {action}: Windows only granted a zero-access handle to this collection. Use feature reports or match the vendor collection instead.");
        }
    }

    private static HidException LastError(string api)
    {
        int error = Marshal.GetLastPInvokeError();
        return new HidException($"{api} failed: {new Win32Exception(error).Message} (0x{error:X})", new Win32Exception(error));
    }
}
