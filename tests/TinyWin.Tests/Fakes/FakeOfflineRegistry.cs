using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;

namespace TinyWin.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IOfflineRegistry"/> that records what was applied.
/// </summary>
/// <remarks>
/// Deliberately tracks whether the session was disposed. A hive session that outlives its stage is
/// the failure mode docs/PLAN.md section 3.3 is about, and a fake that cannot detect it would let
/// that bug through the tests it exists to catch.
/// </remarks>
public sealed class FakeOfflineRegistry : IOfflineRegistry
{
    private readonly List<ComponentAction> _applied = [];

    public IReadOnlyList<ComponentAction> Applied => _applied;

    public List<FakeHiveSession> Sessions { get; } = [];

    /// <summary>Set to make <see cref="OpenAsync"/> throw, to exercise the discard path.</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>Packages the caller should be told are absent, to exercise no-target reporting.</summary>
    public HashSet<string> MissingKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int StrandedHivesToReport { get; set; }

    /// <summary>Runs before each apply, so a test can cancel while a hive session is open.</summary>
    public Action? OnApply { get; set; }

    public bool AllSessionsDisposed => Sessions.All(s => s.Disposed);

    public Task<IHiveSession> OpenAsync(
        string mountPath,
        IReadOnlyCollection<RegistryHive> hives,
        CancellationToken cancellationToken = default)
    {
        if (OpenFailure is not null)
        {
            return Task.FromException<IHiveSession>(OpenFailure);
        }

        var session = new FakeHiveSession(hives, _applied, MissingKeys) { OnApply = OnApply };
        Sessions.Add(session);
        return Task.FromResult<IHiveSession>(session);
    }

    public Task<int> UnloadStrandedHivesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(StrandedHivesToReport);
}

public sealed class FakeHiveSession(
    IReadOnlyCollection<RegistryHive> hives,
    List<ComponentAction> sink,
    HashSet<string> missingKeys) : IHiveSession
{
    public IReadOnlyCollection<RegistryHive> LoadedHives { get; } = hives;

    public bool Disposed { get; private set; }

    public Action? OnApply { get; init; }

    public Task<ActionStatus> ApplyAsync(
        string componentId, ComponentAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        ObjectDisposedException.ThrowIf(Disposed, this);

        OnApply?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        sink.Add(action);

        return Task.FromResult(
            action.Key is not null && missingKeys.Contains(action.Key)
                ? ActionStatus.NoTarget
                : ActionStatus.Applied);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
