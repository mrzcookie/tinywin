using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Win32Registry = Microsoft.Win32.Registry;
using Win32ValueKind = Microsoft.Win32.RegistryValueKind;

namespace TinyWin.Registry.Interop;

/// <summary>
/// The real Win32 implementation of <see cref="INativeRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>RegLoadKey</c> and not <c>RegLoadAppKey</c>.</b> <c>RegLoadAppKey</c> is the tempting
/// alternative: it loads a hive into a private per-process namespace instead of publishing a
/// mount point under <c>HKLM</c>, and the hive is released automatically when the last handle to
/// it closes — including when the process dies. On the face of it that deletes the entire class of
/// failure docs/PLAN.md section 3.3 is about, because a crashed TinyWin could not strand a hive.
/// It was considered and rejected, for three reasons:
/// </para>
/// <list type="number">
/// <item>
/// <b>It moves the failure somewhere the user cannot reach.</b> A leaked handle blocks the WIM
/// dismount either way — that risk does not go away, it just becomes invisible. With a named
/// <c>HKLM</c> mount the user (and our own preflight) can see the stranded hive in regedit and fix
/// it with <c>reg unload</c>. With an app hive there is nothing to look at and nothing to unload;
/// the only recovery is killing the process, and if the handle leak is in a still-running process
/// the user has no way to diagnose why their dismount fails.
/// </item>
/// <item>
/// <b>Crash recovery is a stated requirement, not an accident.</b>
/// <see cref="INativeRegistry.GetLoadedHiveNames"/> exists to support
/// <c>IOfflineRegistry.UnloadStrandedHivesAsync</c>, which is part of the Core preflight contract.
/// It also cleans up after the <i>other</i> tools in this space — tiny11-style scripts leave
/// <c>z</c>-prefixed mounts behind constantly — which an app-hive design could not do.
/// </item>
/// <item>
/// <b>It would not let us skip the privilege work anyway.</b> The backup/restore privileges are
/// needed a second time, independently of loading, to open ACL-protected keys <i>inside</i> the
/// hive with <see cref="NativeMethods.RegOptionBackupRestore"/>. Choosing
/// <c>RegLoadAppKey</c> would remove one caller of <see cref="EnableHivePrivileges"/>, not the
/// method.
/// </item>
/// </list>
/// <para>
/// Against that, the honest counterpoint: <c>RegLoadKey</c>'s failure mode is worse when it does
/// fail, because a stranded named hive survives process exit and can require a reboot. The
/// decision here is to prefer a visible, repairable failure over an invisible, self-healing one,
/// and to spend the effort on making unload reliable (forced finalization, retry with backoff,
/// throwing rather than lying) instead. Because the choice sits behind
/// <see cref="INativeRegistry"/>, it is reversible by writing a second implementation — the
/// session and action layers above would not change. Neither path has been verified on this
/// machine: DISM refuses to run unelevated (error 740), so no hive has actually been loaded.
/// </para>
/// <para>
/// <b>Handle discipline.</b> Every <see cref="RegistryKey"/> in this file is created and disposed
/// inside a single method. None is cached, returned, or stored in a field. That is what makes
/// docs/PLAN.md section 3.3's "no raw RegistryKey may escape" rule structural rather than a matter
/// of care.
/// </para>
/// </remarks>
internal sealed class Win32NativeRegistry : INativeRegistry
{
    private readonly Lock _privilegeLock = new();
    private bool _privilegesEnabled;

    public void EnableHivePrivileges()
    {
        lock (_privilegeLock)
        {
            if (_privilegesEnabled)
            {
                return;
            }

            if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery,
                out var token))
            {
                throw Win32ErrorMapper.PrivilegeFailed(Marshal.GetLastPInvokeError(), "the process token");
            }

            try
            {
                EnablePrivilege(token, NativeMethods.SeBackupName);
                EnablePrivilege(token, NativeMethods.SeRestoreName);
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }

            _privilegesEnabled = true;
        }
    }

    public void LoadHive(string mountName, string hiveFilePath)
    {
        var status = NativeMethods.RegLoadKey(NativeMethods.HkeyLocalMachine, mountName, hiveFilePath);
        if (status != NativeMethods.ErrorSuccess)
        {
            throw Win32ErrorMapper.LoadFailed(status, mountName, hiveFilePath);
        }
    }

    public void UnloadHive(string mountName)
    {
        var status = NativeMethods.RegUnLoadKey(NativeMethods.HkeyLocalMachine, mountName);
        if (status != NativeMethods.ErrorSuccess)
        {
            throw Win32ErrorMapper.UnloadFailed(status, mountName);
        }
    }

    public IReadOnlyList<string> GetLoadedHiveNames() =>
        // HKLM itself is a base key: enumerating it pins nothing, so this is safe to call during
        // preflight without risking the very problem it is looking for.
        Win32Registry.LocalMachine.GetSubKeyNames();

    public bool KeyExists(string keyPath)
    {
        using var key = OpenKey(keyPath, NativeMethods.KeyRead);
        return key is not null;
    }

    public void CreateKey(string keyPath)
    {
        var status = NativeMethods.RegCreateKeyEx(
            NativeMethods.HkeyLocalMachine,
            keyPath,
            reserved: 0,
            lpClass: null,
            NativeMethods.RegOptionBackupRestore,
            NativeMethods.KeyRead | NativeMethods.KeyWrite,
            lpSecurityAttributes: 0,
            out var raw,
            out _);

        if (status != NativeMethods.ErrorSuccess)
        {
            throw Win32ErrorMapper.KeyOperationFailed(status, "create", keyPath);
        }

        // Created and closed immediately — the caller gets nothing that could outlive this call.
        using var handle = new SafeRegistryHandle(raw, ownsHandle: true);
    }

    public void SetValue(string keyPath, string valueName, object data, Win32ValueKind kind)
    {
        using var key = OpenKey(keyPath, NativeMethods.KeyRead | NativeMethods.KeyWrite)
            ?? throw Win32ErrorMapper.KeyOperationFailed(NativeMethods.ErrorFileNotFound, "set value on", keyPath);

        key.SetValue(valueName, data, kind);
    }

    public bool DeleteKeyTree(string keyPath)
    {
        if (!KeyExists(keyPath))
        {
            return false;
        }

        DeleteKeyTreeCore(keyPath);
        return true;
    }

    public bool DeleteValue(string keyPath, string valueName)
    {
        using var key = OpenKey(keyPath, NativeMethods.KeyRead | NativeMethods.KeyWrite);
        if (key is null)
        {
            return false;
        }

        // throwOnMissingValue: false is the whole point — a missing value is a NoTarget for the
        // caller to report, not an exception.
        var existed = key.GetValue(valueName, null) is not null
            || key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);

        key.DeleteValue(valueName, throwOnMissingValue: false);
        return existed;
    }

    private static void EnablePrivilege(nint token, string privilegeName)
    {
        if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out var luid))
        {
            throw Win32ErrorMapper.PrivilegeFailed(Marshal.GetLastPInvokeError(), privilegeName);
        }

        var privileges = new NativeMethods.TokenPrivileges
        {
            PrivilegeCount = 1,
            Privileges = new NativeMethods.LuidAndAttributes
            {
                Luid = luid,
                Attributes = NativeMethods.SePrivilegeEnabled,
            },
        };

        var adjusted = NativeMethods.AdjustTokenPrivileges(
            token,
            disableAllPrivileges: false,
            in privileges,
            (uint)Marshal.SizeOf<NativeMethods.TokenPrivileges>(),
            previousState: 0,
            returnLength: 0);

        // AdjustTokenPrivileges reports success even when it assigned nothing. Checking the last
        // error is not optional here: skipping it is how a tool ends up believing it holds
        // SeRestorePrivilege right up until RegLoadKey returns ERROR_ACCESS_DENIED.
        var error = Marshal.GetLastPInvokeError();
        if (!adjusted || error != NativeMethods.ErrorSuccess)
        {
            throw Win32ErrorMapper.PrivilegeFailed(error, privilegeName);
        }
    }

    /// <summary>
    /// Opens a key under <c>HKLM</c> with backup/restore semantics, or returns null if it does not
    /// exist. Callers must wrap the result in <c>using</c>; nothing may hold on to it.
    /// </summary>
    private static RegistryKey? OpenKey(string keyPath, int access)
    {
        var status = NativeMethods.RegOpenKeyEx(
            NativeMethods.HkeyLocalMachine, keyPath, NativeMethods.RegOptionBackupRestore, access, out var raw);

        if (status is NativeMethods.ErrorFileNotFound)
        {
            return null;
        }

        if (status != NativeMethods.ErrorSuccess)
        {
            throw Win32ErrorMapper.KeyOperationFailed(status, "open", keyPath);
        }

        // Ownership transfers to the RegistryKey; disposing it closes the handle. If FromHandle
        // were to throw, the SafeHandle's own finalizer still closes it.
        return RegistryKey.FromHandle(new SafeRegistryHandle(raw, ownsHandle: true));
    }

    /// <summary>
    /// Depth-first delete. <c>RegDeleteKeyEx</c> refuses a key that still has subkeys, and every
    /// handle is closed before its key is deleted.
    /// </summary>
    private static void DeleteKeyTreeCore(string keyPath)
    {
        string[] children;
        using (var key = OpenKey(keyPath, NativeMethods.KeyRead))
        {
            if (key is null)
            {
                return;
            }

            children = key.GetSubKeyNames();
        }

        foreach (var child in children)
        {
            DeleteKeyTreeCore($"{keyPath}\\{child}");
        }

        var status = NativeMethods.RegDeleteKeyEx(NativeMethods.HkeyLocalMachine, keyPath, samDesired: 0, reserved: 0);
        if (status is not NativeMethods.ErrorSuccess and not NativeMethods.ErrorFileNotFound)
        {
            throw Win32ErrorMapper.KeyOperationFailed(status, "delete", keyPath);
        }
    }
}
