namespace TinyWin.Registry;

/// <summary>
/// A catalog action could not be carried out because its own data is wrong — a missing hive, a
/// malformed key path, a value payload that does not match its declared kind.
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="RegistryOperationException"/>: this one means the catalog
/// is at fault and the fix is a JSON edit, not a retry.
/// </remarks>
public sealed class RegistryActionException : Exception
{
    public RegistryActionException()
    {
    }

    public RegistryActionException(string message)
        : base(message)
    {
    }

    public RegistryActionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A Win32 registry call failed. Carries the raw error code so callers can distinguish the ones
/// that matter (<c>ERROR_ACCESS_DENIED</c>, <c>ERROR_FILE_NOT_FOUND</c>) from the rest.
/// </summary>
public sealed class RegistryOperationException : Exception
{
    public RegistryOperationException()
    {
    }

    public RegistryOperationException(string message)
        : base(message)
    {
    }

    public RegistryOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RegistryOperationException(string message, int errorCode)
        : base(message) => ErrorCode = errorCode;

    /// <summary>The Win32 error code, or 0 when the failure did not come from a Win32 call.</summary>
    public int ErrorCode { get; }
}

/// <summary>
/// One or more hives could not be unloaded, even after forcing finalizers and retrying.
/// </summary>
/// <remarks>
/// This is the failure docs/PLAN.md section 3.3 is written about. It is thrown rather than
/// swallowed because a hive that stays mounted blocks the WIM dismount, and reporting success
/// would leave the user with a machine that needs a reboot and no idea why.
/// </remarks>
public sealed class HiveUnloadException : Exception
{
    public HiveUnloadException()
        : this([])
    {
    }

    public HiveUnloadException(string message)
        : base(message) => MountNames = [];

    public HiveUnloadException(string message, Exception innerException)
        : base(message, innerException) => MountNames = [];

    public HiveUnloadException(IReadOnlyList<string> mountNames)
        : base(BuildMessage(mountNames)) => MountNames = mountNames;

    public HiveUnloadException(IReadOnlyList<string> mountNames, Exception innerException)
        : base(BuildMessage(mountNames), innerException) => MountNames = mountNames;

    /// <summary>The <c>HKLM</c> mount points still loaded when this was thrown.</summary>
    public IReadOnlyList<string> MountNames { get; }

    private static string BuildMessage(IReadOnlyList<string> mountNames)
    {
        ArgumentNullException.ThrowIfNull(mountNames);

        if (mountNames.Count == 0)
        {
            return "One or more offline registry hives could not be unloaded.";
        }

        var names = string.Join(", ", mountNames.Select(n => $"HKLM\\{n}"));
        return $"Could not unload offline registry hive(s): {names}. The image cannot be dismounted "
            + $"while they are loaded. Close any process browsing them (regedit, antivirus) and run "
            + $"'reg unload HKLM\\{mountNames[0]}', or reboot.";
    }
}
