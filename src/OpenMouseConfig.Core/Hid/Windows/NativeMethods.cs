using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenMouseConfig.Core.Hid.Windows;

/// <summary>
/// P/Invoke declarations for hid.dll, cfgmgr32.dll and kernel32.dll.
/// This is the same API surface Chromium's WebHID backend uses on Windows, so device
/// visibility and report semantics match what mouse.xyz sees in the browser.
/// </summary>
/// <remarks>
/// Note on return types: HidD_* functions return BOOLEAN (1 byte), not BOOL (4 bytes), so they
/// must be marshalled with <c>[return: MarshalAs(UnmanagedType.U1)]</c> or garbage in the upper
/// bytes of EAX can turn a failure into "success".
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;

    public const int ErrorIoPending = 997;
    public const int HidpStatusSuccess = 0x00110000;

    public const int CrSuccess = 0x00;
    public const int CrBufferSmall = 0x1A;
    public const int CmGetDeviceInterfaceListPresent = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    public struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    /// <summary>HIDP_CAPS from hidpi.h. Report byte lengths include the report ID byte.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    // ---- hid.dll -------------------------------------------------------------------------

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetSerialNumberString(SafeFileHandle device, byte[] buffer, int bufferLength);

    /// <summary>SET_REPORT(Feature). Buffer[0] = report ID; length must be FeatureReportByteLength.</summary>
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, int bufferLength);

    /// <summary>GET_REPORT(Feature). Caller sets buffer[0] = report ID before the call.</summary>
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FlushQueue(SafeFileHandle device);

    // ---- cfgmgr32.dll (device interface enumeration) ------------------------------------

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid interfaceClassGuid, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId, [Out] char[] buffer, int bufferLength, int flags);

    // ---- kernel32.dll ---------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool ReadFile(SafeFileHandle file, byte* buffer, int numberOfBytesToRead, IntPtr numberOfBytesRead, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool WriteFile(SafeFileHandle file, byte* buffer, int numberOfBytesToWrite, IntPtr numberOfBytesWritten, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out int numberOfBytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>Opens a HID device path. <paramref name="access"/> = 0 is enough for attributes, caps and feature reports.</summary>
    public static SafeFileHandle OpenDevice(string path, uint access, bool overlapped) =>
        CreateFile(
            path,
            access,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            overlapped ? FileFlagOverlapped : 0,
            IntPtr.Zero);

    /// <summary>All present device interfaces of a class (here: the HID class GUID).</summary>
    public static IReadOnlyList<string> GetDeviceInterfaces(Guid interfaceClass)
    {
        // The list can grow between the size query and the fetch (device plugged in), hence the loop.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int result = CM_Get_Device_Interface_List_SizeW(out int length, ref interfaceClass, null, CmGetDeviceInterfaceListPresent);
            if (result != CrSuccess)
            {
                throw new HidException($"CM_Get_Device_Interface_List_Size failed (CONFIGRET 0x{result:X}).");
            }

            if (length <= 1)
            {
                return [];
            }

            var buffer = new char[length];
            result = CM_Get_Device_Interface_ListW(ref interfaceClass, null, buffer, length, CmGetDeviceInterfaceListPresent);
            if (result == CrBufferSmall)
            {
                continue;
            }

            if (result != CrSuccess)
            {
                throw new HidException($"CM_Get_Device_Interface_List failed (CONFIGRET 0x{result:X}).");
            }

            // REG_MULTI_SZ style: "path1\0path2\0\0"
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        throw new HidException("Device list kept changing while enumerating HID devices.");
    }
}
