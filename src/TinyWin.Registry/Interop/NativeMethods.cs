using System.Runtime.InteropServices;

namespace TinyWin.Registry.Interop;

/// <summary>
/// Raw P/Invoke declarations. Nothing here has any policy in it; see
/// <see cref="Win32NativeRegistry"/> for the behaviour and <see cref="INativeRegistry"/> for why
/// this is the only file in the project that talks to Win32.
/// </summary>
internal static partial class NativeMethods
{
    public static readonly nint HkeyLocalMachine = unchecked((nint)(int)0x80000002);

    public const int ErrorSuccess = 0;
    public const int ErrorFileNotFound = 2;
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorBadDatabase = 1009;
    public const int ErrorKeyDeleted = 1018;
    public const int ErrorNotAllAssigned = 1300;
    public const int ErrorPrivilegeNotHeld = 1314;

    public const int KeyRead = 0x00020019;
    public const int KeyWrite = 0x00020006;

    /// <summary>
    /// <c>REG_OPTION_BACKUP_RESTORE</c>. Opens a key using <c>SeBackupPrivilege</c> /
    /// <c>SeRestorePrivilege</c> instead of its ACL, which is what lets us write to keys inside an
    /// offline hive that are owned by TrustedInstaller.
    /// </summary>
    public const uint RegOptionBackupRestore = 0x00000004;

    public const uint TokenAdjustPrivileges = 0x0020;
    public const uint TokenQuery = 0x0008;
    public const uint SePrivilegeEnabled = 0x00000002;

    public const string SeBackupName = "SeBackupPrivilege";
    public const string SeRestoreName = "SeRestorePrivilege";

    [LibraryImport("advapi32.dll", EntryPoint = "RegLoadKeyW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegLoadKey(nint hKey, string lpSubKey, string lpFile);

    [LibraryImport("advapi32.dll", EntryPoint = "RegUnLoadKeyW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegUnLoadKey(nint hKey, string lpSubKey);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegOpenKeyEx(
        nint hKey, string lpSubKey, uint ulOptions, int samDesired, out nint phkResult);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegCreateKeyEx(
        nint hKey,
        string lpSubKey,
        int reserved,
        string? lpClass,
        uint dwOptions,
        int samDesired,
        nint lpSecurityAttributes,
        out nint phkResult,
        out uint lpdwDisposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegDeleteKeyEx(nint hKey, string lpSubKey, int samDesired, int reserved);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? lpSystemName, string lpName, out Luid lpLuid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        in TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    /// <summary>
    /// <c>TOKEN_PRIVILEGES</c> with its variable-length array fixed at one entry. We only ever
    /// adjust one privilege per call, which keeps this blittable and the marshalling trivial.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }
}
