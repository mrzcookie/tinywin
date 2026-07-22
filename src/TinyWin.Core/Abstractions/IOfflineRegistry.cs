using TinyWin.Catalog.Models;
using TinyWin.Core.Models;

namespace TinyWin.Core.Abstractions;

/// <summary>
/// A scoped load of the offline hives, which guarantees they are unloaded again.
/// </summary>
/// <remarks>
/// Hives that refuse to unload are the classic failure of this class of tool — see
/// docs/PLAN.md section 3.3. Two rules make the contract work:
///
/// 1. No raw registry handle may escape this interface. Callers get <see cref="ApplyAsync"/> and
///    nothing else, so a stray managed finalizer cannot pin a hive open.
/// 2. <see cref="DisposeAsync"/> must unload every hive it loaded, retrying after a forced GC,
///    and must throw if it ultimately cannot — a silent failure here strands the whole image.
/// </remarks>
public interface IHiveSession : IAsyncDisposable
{
    /// <summary>Hives actually loaded by this session.</summary>
    IReadOnlyCollection<RegistryHive> LoadedHives { get; }

    Task<ActionStatus> ApplyAsync(
        string componentId, ComponentAction action, CancellationToken cancellationToken = default);
}

public interface IOfflineRegistry
{
    /// <summary>
    /// Loads the requested hives from a mounted image. Enables SeBackupPrivilege and
    /// SeRestorePrivilege on the process token first; both are present but disabled by default
    /// even in an elevated process.
    /// </summary>
    Task<IHiveSession> OpenAsync(
        string mountPath,
        IReadOnlyCollection<RegistryHive> hives,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads hives stranded by a previous crashed run. Part of preflight recovery — without it,
    /// a crash leaves the machine unable to start a new build until reboot.
    /// </summary>
    Task<int> UnloadStrandedHivesAsync(CancellationToken cancellationToken = default);
}
