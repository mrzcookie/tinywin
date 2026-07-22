using System.Globalization;
using Microsoft.Win32.SafeHandles;
using TinyWin.IsoBuilder.Interop;

namespace TinyWin.IsoBuilder;

/// <summary>
/// Attaches an ISO read-only and exposes the drive letter Windows assigned it. Detaches on dispose.
/// </summary>
/// <remarks>
/// <para>
/// This exists because xorriso cannot read a Windows 11 ISO's contents. Genuine Microsoft media
/// carries a decoy ISO 9660 tree holding one file — <c>README.TXT</c> — with everything real
/// living in the UDF tree, which libisofs does not implement. Verified against 25H2 (26200):
/// <c>xorriso -indev &lt;iso&gt; -lsl /</c> returns <c>total 1</c>. Extraction therefore goes
/// through Windows' own UDF reader.
/// </para>
/// <para>
/// The attach is not made permanent, so the image detaches when the handle closes even if the
/// process dies — cancellation cannot strand a mounted image.
/// </para>
/// </remarks>
internal sealed class IsoImageMount : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private IsoImageMount(SafeFileHandle handle, char driveLetter, string physicalPath)
    {
        _handle = handle;
        DriveLetter = driveLetter;
        PhysicalPath = physicalPath;
    }

    public char DriveLetter { get; }

    public string PhysicalPath { get; }

    public string RootPath => $"{DriveLetter}:\\";

    public static IsoImageMount Attach(string isoPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);

        var fullPath = Path.GetFullPath(isoPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"ISO '{fullPath}' does not exist.", fullPath);
        }

        var storageType = new VirtualStorageType
        {
            DeviceId = NativeMethods.VirtualStorageTypeDeviceIso,
            VendorId = NativeMethods.VirtualStorageTypeVendorMicrosoft,
        };

        var open = NativeMethods.OpenVirtualDisk(
            ref storageType, fullPath, NativeMethods.VirtualDiskAccessRead, 0, 0, out var handle);

        if (open != NativeMethods.ErrorSuccess)
        {
            handle.Dispose();
            throw new IsoBuilderException(
                $"Could not open '{fullPath}' as a disk image (OpenVirtualDisk returned {open}). " +
                "The file may not be a valid ISO.");
        }

        var attach = NativeMethods.AttachVirtualDisk(
            handle, 0, NativeMethods.AttachVirtualDiskFlagReadOnly, 0, 0, 0);

        if (attach != NativeMethods.ErrorSuccess)
        {
            handle.Dispose();
            throw new IsoBuilderException(
                $"Could not mount '{fullPath}' (AttachVirtualDisk returned {attach}).");
        }

        try
        {
            var physicalPath = ReadPhysicalPath(handle);
            var letter = WaitForDriveLetter(physicalPath, cancellationToken);
            return new IsoImageMount(handle, letter, physicalPath);
        }
        catch
        {
            Detach(handle);
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach(_handle);
        _handle.Dispose();
    }

    /// <summary>
    /// Detaching can only fail if the image is already gone, and the handle close below detaches it
    /// anyway — the attach was never made permanent. So the code is recorded, not acted on.
    /// </summary>
    private static void Detach(SafeFileHandle handle) => _ = NativeMethods.DetachVirtualDisk(handle, 0, 0);

    private static string ReadPhysicalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        var size = (uint)(buffer.Length * sizeof(char));

        var result = NativeMethods.GetVirtualDiskPhysicalPath(handle, ref size, buffer);
        if (result != NativeMethods.ErrorSuccess)
        {
            throw new IsoBuilderException(
                $"Mounted the image but could not identify its device (GetVirtualDiskPhysicalPath returned {result}).");
        }

        var text = new string(buffer);
        var terminator = text.IndexOf('\0', StringComparison.Ordinal);
        return terminator >= 0 ? text[..terminator] : text;
    }

    /// <summary>
    /// Resolves <c>\\.\CDROM1</c> to a drive letter by storage device number.
    /// </summary>
    /// <remarks>
    /// Matching on volume label would be wrong: two copies of the same ISO mounted at once present
    /// identical labels, which is not hypothetical — it happened on the development machine.
    /// </remarks>
    private static char WaitForDriveLetter(string physicalPath, CancellationToken cancellationToken)
    {
        var target = ReadDeviceNumber(physicalPath)
            ?? throw new IsoBuilderException($"Could not read the device number of '{physicalPath}'.");

        // The volume arrives asynchronously after the attach returns.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.CDRom)
                {
                    continue;
                }

                var letter = drive.Name[0];
                var number = ReadDeviceNumber($@"\\.\{letter}:");
                if (number == target)
                {
                    return letter;
                }
            }

            Thread.Sleep(250);
        }

        throw new IsoBuilderException(
            $"The image attached as '{physicalPath}' but no drive letter appeared within 15 seconds.");
    }

    private static uint? ReadDeviceNumber(string devicePath)
    {
        using var device = NativeMethods.CreateFile(
            devicePath,
            0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            0,
            NativeMethods.OpenExisting,
            0,
            0);

        if (device.IsInvalid)
        {
            return null;
        }

        var ok = NativeMethods.DeviceIoControl(
            device,
            NativeMethods.IoctlStorageGetDeviceNumber,
            0,
            0,
            out var number,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<StorageDeviceNumber>(),
            out _,
            0);

        return ok ? number.DeviceNumber : null;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{PhysicalPath} -> {RootPath}");
}
