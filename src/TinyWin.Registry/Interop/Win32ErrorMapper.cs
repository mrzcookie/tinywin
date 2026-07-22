using System.ComponentModel;

namespace TinyWin.Registry.Interop;

/// <summary>
/// Turns an <c>LSTATUS</c> into an exception a user can act on.
/// </summary>
/// <remarks>
/// Worth its own type because the two codes that matter here mean something very specific in this
/// context and nothing like it elsewhere: <c>ERROR_ACCESS_DENIED</c> from <c>RegLoadKey</c> almost
/// always means the backup/restore privileges were never enabled, and <c>ERROR_ACCESS_DENIED</c>
/// from <c>RegUnLoadKey</c> almost always means something still holds a key open. A bare
/// "Access is denied." sends the user in the wrong direction for both.
/// </remarks>
internal static class Win32ErrorMapper
{
    public static RegistryOperationException LoadFailed(int error, string mountName, string hiveFilePath)
    {
        var detail = error switch
        {
            NativeMethods.ErrorAccessDenied =>
                "Access denied. This normally means SeBackupPrivilege/SeRestorePrivilege are not enabled on the "
                + "process token, or the process is not elevated.",
            NativeMethods.ErrorFileNotFound =>
                "The hive file does not exist. Is the image actually mounted at that path?",
            NativeMethods.ErrorSharingViolation =>
                "The hive file is in use. Another tool may already have it loaded.",
            NativeMethods.ErrorBadDatabase =>
                "The file is not a valid registry hive, or it needs log recovery.",
            NativeMethods.ErrorInvalidParameter =>
                "Invalid parameter. A hive is already loaded at this mount point, or the mount name is malformed.",
            _ => Describe(error),
        };

        return new RegistryOperationException(
            $"Could not load hive '{hiveFilePath}' as HKLM\\{mountName}: {detail} (error {error})", error);
    }

    public static RegistryOperationException UnloadFailed(int error, string mountName)
    {
        var detail = error switch
        {
            NativeMethods.ErrorAccessDenied =>
                "Access denied. Something still holds a key open under this hive — the usual culprit is a "
                + "RegistryKey awaiting finalization, but regedit or an antivirus scanner will do it too.",
            NativeMethods.ErrorFileNotFound =>
                "No hive is loaded at that mount point.",
            NativeMethods.ErrorInvalidParameter =>
                "Invalid parameter. No hive is loaded at that mount point.",
            _ => Describe(error),
        };

        return new RegistryOperationException($"Could not unload HKLM\\{mountName}: {detail} (error {error})", error);
    }

    public static RegistryOperationException KeyOperationFailed(int error, string operation, string keyPath)
    {
        var detail = error switch
        {
            NativeMethods.ErrorAccessDenied =>
                "Access denied. The key's ACL denies this even with backup/restore privileges — a key owned by "
                + "TrustedInstaller needs its ownership taken first, which TinyWin does not yet do.",
            NativeMethods.ErrorKeyDeleted => "The key was deleted while it was open.",
            _ => Describe(error),
        };

        return new RegistryOperationException(
            $"Registry {operation} failed for HKLM\\{keyPath}: {detail} (error {error})", error);
    }

    public static RegistryOperationException PrivilegeFailed(int error, string privilege)
    {
        var detail = error switch
        {
            NativeMethods.ErrorNotAllAssigned =>
                "The privilege is not held by this token. TinyWin must run elevated as an administrator.",
            NativeMethods.ErrorPrivilegeNotHeld =>
                "The privilege is not held by this token. TinyWin must run elevated as an administrator.",
            _ => Describe(error),
        };

        return new RegistryOperationException($"Could not enable {privilege}: {detail} (error {error})", error);
    }

    private static string Describe(int error) => new Win32Exception(error).Message;
}
