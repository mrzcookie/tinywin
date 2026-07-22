using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TinyWin.IsoBuilder.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct VirtualStorageType
{
    public uint DeviceId;
    public Guid VendorId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageDeviceNumber
{
    public uint DeviceType;
    public uint DeviceNumber;
    public int PartitionNumber;
}

/// <summary>
/// The subset of virtdisk.dll and kernel32.dll needed to attach an ISO read-only and find the
/// drive letter Windows gave it.
/// </summary>
/// <remarks>
/// Attaching an ISO through this API does <em>not</em> require elevation — verified on this
/// machine against real 25H2 media in an unelevated session. That matters, because TinyWin needs
/// admin for DISM anyway but must be able to inspect media before asking for it.
/// </remarks>
internal static class NativeMethods
{
    /// <summary>VIRTUAL_STORAGE_TYPE_DEVICE_ISO.</summary>
    public const uint VirtualStorageTypeDeviceIso = 1;

    /// <summary>VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT.</summary>
    public static readonly Guid VirtualStorageTypeVendorMicrosoft =
        new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    /// <summary>VIRTUAL_DISK_ACCESS_READ.</summary>
    public const uint VirtualDiskAccessRead = 0x000d0000;

    /// <summary>ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY.</summary>
    public const uint AttachVirtualDiskFlagReadOnly = 0x00000001;

    /// <summary>IOCTL_STORAGE_GET_DEVICE_NUMBER.</summary>
    public const uint IoctlStorageGetDeviceNumber = 0x002d1080;

    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;

    public const int ErrorSuccess = 0;

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    public static extern int OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        uint flags,
        nint parameters,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll", ExactSpelling = true, SetLastError = false)]
    public static extern int AttachVirtualDisk(
        SafeFileHandle handle,
        nint securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        nint parameters,
        nint overlapped);

    [DllImport("virtdisk.dll", ExactSpelling = true, SetLastError = false)]
    public static extern int DetachVirtualDisk(SafeFileHandle handle, uint flags, uint providerSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    public static extern int GetVirtualDiskPhysicalPath(
        SafeFileHandle handle,
        ref uint diskPathSizeInBytes,
        [Out] char[] diskPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint inBuffer,
        uint inBufferSize,
        out StorageDeviceNumber outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
