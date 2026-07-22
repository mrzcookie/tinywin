using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Registry.Interop;

namespace TinyWin.Registry;

/// <summary>
/// Loads and unloads the offline hives of a mounted image. See
/// <see cref="IOfflineRegistry"/> and docs/PLAN.md section 3.3.
/// </summary>
public sealed class OfflineRegistry : IOfflineRegistry
{
    private readonly INativeRegistry _native;
    private readonly HiveUnloadPolicy _policy;

    public OfflineRegistry()
        : this(new Win32NativeRegistry(), HiveUnloadPolicy.Default)
    {
    }

    public OfflineRegistry(HiveUnloadPolicy policy)
        : this(new Win32NativeRegistry(), policy)
    {
    }

    internal OfflineRegistry(INativeRegistry native, HiveUnloadPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(policy);

        _native = native;
        _policy = policy;
    }

    /// <inheritdoc />
    public async Task<IHiveSession> OpenAsync(
        string mountPath,
        IReadOnlyCollection<RegistryHive> hives,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPath);
        ArgumentNullException.ThrowIfNull(hives);
        cancellationToken.ThrowIfCancellationRequested();

        // Before anything else, and unconditionally. SeBackupPrivilege and SeRestorePrivilege are
        // present but disabled by default even in an elevated process, and a RegLoadKey issued
        // without them fails with a bare ERROR_ACCESS_DENIED that reads like an elevation problem.
        _native.EnableHivePrivileges();

        var session = new HiveSession(_native, _policy);

        try
        {
            foreach (var hive in hives.Distinct().Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                _native.LoadHive(HiveLayout.MountName(hive), HiveLayout.FilePath(mountPath, hive));
                session.MarkLoaded(hive);
            }
        }
        catch (Exception ex)
        {
            // A partial load must not leak the hives that did come up — including when the caller
            // cancelled midway.
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (HiveUnloadException unloadEx)
            {
                // Both problems matter, and AggregateException would hide the first one behind
                // "One or more errors occurred" in the build report.
                throw new RegistryOperationException(
                    $"{ex.Message} Cleanup then failed too: {unloadEx.Message}", ex);
            }

            throw;
        }

        return session;
    }

    /// <inheritdoc />
    public async Task<int> UnloadStrandedHivesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _native.EnableHivePrivileges();

        // Only our own mount points. Another tool's loaded hive is not ours to unload, and the
        // prefix check is the whole reason HiveLayout does not use tiny11's bare "zSOFTWARE".
        var stranded = _native.GetLoadedHiveNames().Where(HiveLayout.IsTinyWinMountName).ToList();

        var unloaded = 0;
        var stuck = new List<string>();
        Exception? firstFailure = null;

        foreach (var mountName in stranded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = await HiveUnloader.TryUnloadAsync(_native, mountName, _policy).ConfigureAwait(false);

            if (failure is null)
            {
                unloaded++;
            }
            else
            {
                stuck.Add(mountName);
                firstFailure ??= failure;
            }
        }

        if (stuck.Count > 0)
        {
            // Preflight has to fail loudly here. A hive we cannot unload now is a build that will
            // fail at dismount in twenty minutes' time instead.
            throw new HiveUnloadException(stuck, firstFailure!);
        }

        return unloaded;
    }
}
