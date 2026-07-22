using System.Globalization;

namespace TinyWin.Imaging.Dism;

/// <summary>What a DISM exit code means to TinyWin.</summary>
public enum DismErrorKind
{
    Success,

    /// <summary>3010. The operation worked; the reboot is the host's problem, not an offline image's.</summary>
    RebootRequired,

    /// <summary>740. The single most likely failure, and the one that must not surface as a number.</summary>
    ElevationRequired,

    /// <summary>The thing we asked DISM to remove was not in the image. Maps to <c>ActionStatus.NoTarget</c>.</summary>
    TargetNotFound,

    FileNotFound,
    AccessDenied,

    /// <summary>The image is already mounted, usually by a crashed previous run.</summary>
    ImageAlreadyMounted,

    /// <summary>Something still holds a handle under the mount point — the classic dismount failure.</summary>
    MountPointBusy,

    /// <summary>The mount directory exists and is not empty.</summary>
    MountDirectoryNotEmpty,

    InvalidArgument,
    DiskFull,
    Cancelled,

    /// <summary>DISM needed payload it could not find. Common after an over-aggressive /ResetBase.</summary>
    SourceMissing,

    Unknown,
}

/// <summary>
/// Maps <c>dism.exe</c> exit codes to <see cref="DismErrorKind"/> and to a message a user can act on.
/// </summary>
/// <remarks>
/// <para>DISM exits with either a Win32 code (740) or an HRESULT (0x800F080C), and .NET hands us
/// both as a signed <see cref="int"/>. Everything here compares as <see cref="uint"/> so the
/// HRESULT cases do not have to be written as negative literals.</para>
/// <para>Codes not in the table are not guessed at. They come back as
/// <see cref="DismErrorKind.Unknown"/> with the raw value in hex and decimal, which is what you
/// need to look one up. <c>scripts/Verify-DismBackend.ps1</c> records real codes from an elevated
/// run so this table can grow from evidence rather than from memory.</para>
/// </remarks>
public static class DismExitCode
{
    private static readonly Dictionary<uint, DismErrorKind> Known = new()
    {
        [0x00000000] = DismErrorKind.Success,
        [0x00000BC2] = DismErrorKind.RebootRequired,          // 3010 ERROR_SUCCESS_REBOOT_REQUIRED

        [0x000002E4] = DismErrorKind.ElevationRequired,       // 740  ERROR_ELEVATION_REQUIRED
        [0x800702E4] = DismErrorKind.ElevationRequired,

        [0x00000002] = DismErrorKind.FileNotFound,            // 2    ERROR_FILE_NOT_FOUND
        [0x80070002] = DismErrorKind.FileNotFound,
        [0x00000003] = DismErrorKind.FileNotFound,            // 3    ERROR_PATH_NOT_FOUND
        [0x80070003] = DismErrorKind.FileNotFound,

        [0x00000005] = DismErrorKind.AccessDenied,            // 5    ERROR_ACCESS_DENIED
        [0x80070005] = DismErrorKind.AccessDenied,

        [0x00000020] = DismErrorKind.MountPointBusy,          // 32   ERROR_SHARING_VIOLATION
        [0x80070020] = DismErrorKind.MountPointBusy,

        [0x00000091] = DismErrorKind.MountDirectoryNotEmpty,  // 145  ERROR_DIR_NOT_EMPTY
        [0x80070091] = DismErrorKind.MountDirectoryNotEmpty,

        [0x00000057] = DismErrorKind.InvalidArgument,         // 87   ERROR_INVALID_PARAMETER
        [0x80070057] = DismErrorKind.InvalidArgument,

        [0x00000070] = DismErrorKind.DiskFull,                // 112  ERROR_DISK_FULL
        [0x80070070] = DismErrorKind.DiskFull,

        [0x000004C7] = DismErrorKind.Cancelled,               // 1223 ERROR_CANCELLED
        [0x800704C7] = DismErrorKind.Cancelled,

        [0x00000490] = DismErrorKind.TargetNotFound,          // 1168 ERROR_NOT_FOUND
        [0x80070490] = DismErrorKind.TargetNotFound,
        [0x800F080C] = DismErrorKind.TargetNotFound,          // feature name not recognised
        [0x80073CF1] = DismErrorKind.TargetNotFound,          // appx package not found

        [0x800F081F] = DismErrorKind.SourceMissing,           // CBS_E_SOURCE_MISSING

        [0xC1420127] = DismErrorKind.ImageAlreadyMounted,
    };

    public static DismErrorKind Classify(int exitCode) =>
        Known.TryGetValue(unchecked((uint)exitCode), out var kind) ? kind : DismErrorKind.Unknown;

    /// <summary>3010 is a success. Treating it as a failure is a classic DISM-wrapper bug.</summary>
    public static bool IsSuccess(int exitCode) =>
        Classify(exitCode) is DismErrorKind.Success or DismErrorKind.RebootRequired;

    /// <summary>
    /// True when the failure means "the thing was not there", which is a
    /// <c>ActionStatus.NoTarget</c> rather than an error.
    /// </summary>
    public static bool IsMissingTarget(int exitCode) => Classify(exitCode) == DismErrorKind.TargetNotFound;

    /// <summary>A sentence a user can act on, with the raw code kept for the log.</summary>
    public static string Describe(int exitCode)
    {
        var kind = Classify(exitCode);
        var raw = FormatCode(exitCode);

        return kind switch
        {
            DismErrorKind.Success => $"DISM completed successfully ({raw}).",

            DismErrorKind.RebootRequired =>
                $"DISM completed successfully but asked for a reboot ({raw}). Offline servicing does not " +
                "need one — the image is fine.",

            // Spelled out deliberately: 740 is the failure every user of a tool like this hits first,
            // and "Error: 740" on its own tells them nothing.
            DismErrorKind.ElevationRequired =>
                $"DISM requires Administrator rights ({raw}). Close TinyWin and start it again as " +
                "Administrator. DISM refuses every servicing command without elevation, including " +
                "read-only ones like /Get-WimInfo, so nothing was read or changed.",

            DismErrorKind.TargetNotFound =>
                $"DISM could not find the requested component in the image ({raw}). The catalog entry " +
                "names something this build does not ship.",

            DismErrorKind.FileNotFound =>
                $"DISM could not find a file it needed ({raw}). Check that the image path is correct and " +
                "that the source media is still connected.",

            DismErrorKind.AccessDenied =>
                $"DISM was denied access ({raw}). The image or mount directory may be read-only, on " +
                "read-only media, or locked by another process such as antivirus.",

            DismErrorKind.ImageAlreadyMounted =>
                $"That image is already mounted ({raw}), usually left behind by a previous run that did " +
                "not finish. Run the preflight cleanup (/Cleanup-Mountpoints) and try again.",

            DismErrorKind.MountPointBusy =>
                $"Something still has files open under the mount directory ({raw}). Close any Explorer " +
                "window, shell or antivirus scan on it — an offline registry hive that failed to unload " +
                "is the other usual cause.",

            DismErrorKind.MountDirectoryNotEmpty =>
                $"The mount directory is not empty ({raw}). TinyWin needs an empty directory to mount into.",

            DismErrorKind.InvalidArgument =>
                $"DISM rejected the command line ({raw}). This is a TinyWin bug — please report it with " +
                "the command shown below.",

            DismErrorKind.DiskFull =>
                $"The disk is full ({raw}). Servicing a Windows image needs roughly 25 GB of free space " +
                "for the mount, scratch and export.",

            DismErrorKind.Cancelled => $"The DISM operation was cancelled ({raw}).",

            DismErrorKind.SourceMissing =>
                $"DISM needed component files that are not in the image ({raw}). This usually follows a " +
                "/ResetBase cleanup, which makes removed components unrecoverable.",

            _ => $"DISM failed with {raw}. See the DISM log for detail.",
        };
    }

    /// <summary>Renders a code as both hex and decimal, because DISM documentation uses both.</summary>
    public static string FormatCode(int exitCode)
    {
        var unsigned = unchecked((uint)exitCode);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"exit code {exitCode} / 0x{unsigned:X8}");
    }
}
