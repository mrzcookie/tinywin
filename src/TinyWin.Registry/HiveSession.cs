using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Registry.Interop;

namespace TinyWin.Registry;

/// <summary>
/// A scoped load of the offline hives. See <see cref="IHiveSession"/> for the contract and
/// docs/PLAN.md section 3.3 for why it is shaped this way.
/// </summary>
/// <remarks>
/// Internal on purpose: callers get the interface, and the interface exposes no handle, no key,
/// and no way to reach the hive except <see cref="ApplyAsync"/>. Constructing one directly is
/// <see cref="OfflineRegistry"/>'s job, because a session that exists without its hives loaded, or
/// whose hives were loaded without anyone owning the unload, is the bug this whole design is
/// built to make unrepresentable.
/// </remarks>
internal sealed class HiveSession(INativeRegistry native, HiveUnloadPolicy policy) : IHiveSession
{
    private readonly List<RegistryHive> _loaded = [];
    private bool _disposed;

    public IReadOnlyCollection<RegistryHive> LoadedHives => _loaded.AsReadOnly();

    /// <summary>Records a hive as loaded, so teardown knows to unload it.</summary>
    public void MarkLoaded(RegistryHive hive) => _loaded.Add(hive);

    public Task<ActionStatus> ApplyAsync(
        string componentId, ComponentAction action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(action.Type switch
        {
            ActionType.SetRegistry => ApplySetRegistry(componentId, action),
            ActionType.DeleteRegistryKey => ApplyDeleteRegistryKey(componentId, action),
            ActionType.RemoveScheduledTask => ApplyRemoveScheduledTask(componentId, action),
            _ => throw new RegistryActionException(
                $"Component '{componentId}' routed a '{action.Type}' action to the registry engine, which only "
                + "handles setRegistry, deleteRegistryKey and removeScheduledTask."),
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Reverse of load order, and every hive gets its own attempt even after one fails — giving
        // up on the rest would strand more of them than necessary.
        var stuck = new List<string>();
        Exception? firstFailure = null;

        foreach (var hive in Enumerable.Reverse(_loaded).ToList())
        {
            var mountName = HiveLayout.MountName(hive);
            var failure = await HiveUnloader.TryUnloadAsync(native, mountName, policy).ConfigureAwait(false);

            if (failure is null)
            {
                _loaded.Remove(hive);
            }
            else
            {
                stuck.Add(mountName);
                firstFailure ??= failure;
            }
        }

        if (stuck.Count > 0)
        {
            // Never report success here. A hive left loaded blocks the WIM dismount, and a caller
            // that believes teardown worked will report a successful build over a broken machine.
            throw new HiveUnloadException(stuck, firstFailure!);
        }
    }

    /// <summary>
    /// Writes a value, creating the key and any missing ancestors.
    /// </summary>
    /// <remarks>
    /// This is the one registry action that never reports <see cref="ActionStatus.NoTarget"/>, and
    /// the asymmetry is deliberate. Most tweaks are policy values under keys Windows only
    /// materialises once something sets them — <c>Policies\Microsoft\Windows\Explorer</c> does not
    /// exist on stock media — so treating a missing key as a missing target would make every
    /// working tweak report a no-op and drown the signal that docs/PLAN.md section 2.1 is trying to
    /// preserve. The honest no-op question for a write is "did this change anything", which needs a
    /// before/after diff the catalog does not currently ask for; the deletions below carry the
    /// no-target reporting instead.
    /// </remarks>
    private ActionStatus ApplySetRegistry(string componentId, ComponentAction action)
    {
        var keyPath = ResolveKeyPath(componentId, action);
        var kind = action.Kind
            ?? throw new RegistryActionException($"Component '{componentId}': setRegistry requires 'kind'.");
        var (data, win32Kind) = RegistryValueConverter.Convert(kind, action.Data);

        native.CreateKey(keyPath);
        native.SetValue(keyPath, action.ValueName ?? string.Empty, data, win32Kind);
        return ActionStatus.Applied;
    }

    /// <summary>
    /// Deletes a whole key, or a single value when <c>valueName</c> is set. Reports
    /// <see cref="ActionStatus.NoTarget"/> when there was nothing there — a catalog entry whose
    /// key has been renamed by a Windows update must not look like a successful removal.
    /// </summary>
    private ActionStatus ApplyDeleteRegistryKey(string componentId, ComponentAction action)
    {
        var keyPath = ResolveKeyPath(componentId, action);

        if (!string.IsNullOrEmpty(action.ValueName))
        {
            return native.DeleteValue(keyPath, action.ValueName) ? ActionStatus.Applied : ActionStatus.NoTarget;
        }

        return native.DeleteKeyTree(keyPath) ? ActionStatus.Applied : ActionStatus.NoTarget;
    }

    /// <summary>
    /// Removes a scheduled task by unregistering it from <c>TaskCache</c>, which on an offline
    /// image is where the task actually lives — see <see cref="TaskCache"/> and
    /// docs/catalog-gaps.md section 3.1.
    /// </summary>
    /// <remarks>
    /// One catalog action produces one outcome, which is the reason this lives here rather than
    /// being composed by Core out of smaller registry primitives. Composing it would mean exposing
    /// a hive read on <see cref="IHiveSession"/> just to fetch the task's GUID, and it would report
    /// a half-removed task as several contradictory outcomes instead of one honest status.
    /// </remarks>
    private ActionStatus ApplyRemoveScheduledTask(string componentId, ComponentAction action)
    {
        // The registration is always in SOFTWARE; the catalog does not (and should not) name a
        // hive for a task action.
        var hive = action.Hive ?? RegistryHive.Software;
        RequireLoaded(componentId, hive);

        if (hive != RegistryHive.Software)
        {
            throw new RegistryActionException(
                $"Component '{componentId}': scheduled tasks are registered in the Software hive, not {hive}.");
        }

        var mountName = HiveLayout.MountName(hive);
        string taskName;
        try
        {
            taskName = TaskCache.NormalizeTaskName(action.Name);
        }
        catch (RegistryActionException ex)
        {
            throw new RegistryActionException($"Component '{componentId}': {ex.Message}", ex);
        }

        var treePath = RegistryKeyPath.UnderMount(mountName, TaskCache.TreePath(taskName));
        if (!native.KeyExists(treePath))
        {
            // The task was never registered on this media. Exactly the case docs/PLAN.md section
            // 2.1 wants counted rather than reported as a successful removal.
            return ActionStatus.NoTarget;
        }

        var id = native.GetStringValue(treePath, TaskCache.IdValueName);
        if (string.IsNullOrWhiteSpace(id))
        {
            // Deleting the Tree node now would orphan the Tasks entry with nothing left pointing
            // at it, so refuse and let the build report say why.
            throw new RegistryActionException(
                $"Component '{componentId}': scheduled task '{taskName}' has a TaskCache\\Tree node with no "
                + $"'{TaskCache.IdValueName}' value, so its registration cannot be located. Refusing to delete "
                + "half of it.");
        }

        foreach (var subkey in TaskCache.IdKeyedSubkeys)
        {
            // A task only appears in the trigger indexes it actually uses, so a miss here is
            // normal and not a no-op worth reporting.
            native.DeleteKeyTree(RegistryKeyPath.UnderMount(mountName, TaskCache.IdKeyedPath(subkey, id)));
        }

        // Tree last, deliberately. It is the only thing that maps a task name to its GUID, so a
        // crash partway through leaves the Tree node still pointing at whatever remains and a
        // re-run finishes the job. Deleting Tree first would strand the rest unreachable.
        native.DeleteKeyTree(treePath);
        return ActionStatus.Applied;
    }

    private string ResolveKeyPath(string componentId, ComponentAction action)
    {
        var hive = action.Hive
            ?? throw new RegistryActionException($"Component '{componentId}': {Describe(action.Type)} requires 'hive'.");

        RequireLoaded(componentId, hive);

        try
        {
            return RegistryKeyPath.UnderMount(HiveLayout.MountName(hive), RegistryKeyPath.Normalize(action.Key));
        }
        catch (RegistryActionException ex)
        {
            throw new RegistryActionException($"Component '{componentId}': {ex.Message}", ex);
        }
    }

    private void RequireLoaded(string componentId, RegistryHive hive)
    {
        if (!_loaded.Contains(hive))
        {
            var loaded = _loaded.Count == 0 ? "none" : string.Join(", ", _loaded);
            throw new RegistryActionException(
                $"Component '{componentId}' targets the {hive} hive, but this session loaded {loaded}.");
        }
    }

    private static string Describe(ActionType type) =>
        char.ToLowerInvariant(type.ToString()[0]) + type.ToString()[1..];
}
