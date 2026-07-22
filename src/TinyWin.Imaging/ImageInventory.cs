using TinyWin.Core.Abstractions;
using TinyWin.Imaging.Dism;

namespace TinyWin.Imaging;

/// <summary>
/// What a mounted image currently contains, loaded once per image and kept up to date as actions
/// remove things.
/// </summary>
/// <remarks>
/// <para>This is how <see cref="DismExeBackend"/> answers "was it actually there?" — the question
/// <c>ActionStatus.NoTarget</c> exists to answer (see <c>TinyWin.Core/Models/ActionOutcome.cs</c>).
/// Asking DISM up front is more trustworthy than inferring absence from a removal's error code:
/// error codes vary by component type and DISM version, whereas an enumeration is unambiguous.</para>
/// <para>It is also the difference between a build that finishes and one that does not. A balanced
/// preset runs on the order of a hundred actions; probing before each one would mean a hundred
/// extra <c>dism.exe</c> invocations, each paying full process and provider-store startup. Four
/// enumerations per image, cached, is the whole cost.</para>
/// </remarks>
internal sealed class ImageInventory : IDisposable
{
    private readonly AsyncCache<IReadOnlyList<ProvisionedAppx>> _appx;
    private readonly AsyncCache<IReadOnlyDictionary<string, DismComponentState>> _capabilities;
    private readonly AsyncCache<IReadOnlyDictionary<string, DismComponentState>> _features;
    private readonly AsyncCache<IReadOnlyDictionary<string, DismComponentState>> _packages;

    public ImageInventory(
        Func<CancellationToken, Task<IReadOnlyList<ProvisionedAppx>>> loadAppx,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, DismComponentState>>> loadCapabilities,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, DismComponentState>>> loadFeatures,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, DismComponentState>>> loadPackages)
    {
        _appx = new AsyncCache<IReadOnlyList<ProvisionedAppx>>(loadAppx);
        _capabilities = new AsyncCache<IReadOnlyDictionary<string, DismComponentState>>(loadCapabilities);
        _features = new AsyncCache<IReadOnlyDictionary<string, DismComponentState>>(loadFeatures);
        _packages = new AsyncCache<IReadOnlyDictionary<string, DismComponentState>>(loadPackages);
    }

    public Task<IReadOnlyList<ProvisionedAppx>> GetAppxAsync(CancellationToken ct) => _appx.GetAsync(ct);

    public Task<IReadOnlyDictionary<string, DismComponentState>> GetCapabilitiesAsync(CancellationToken ct) =>
        _capabilities.GetAsync(ct);

    public Task<IReadOnlyDictionary<string, DismComponentState>> GetFeaturesAsync(CancellationToken ct) =>
        _features.GetAsync(ct);

    public Task<IReadOnlyDictionary<string, DismComponentState>> GetPackagesAsync(CancellationToken ct) =>
        _packages.GetAsync(ct);

    public void RemoveAppx(string packageName) =>
        _appx.Update(current =>
            [.. current.Where(p => !string.Equals(p.PackageName, packageName, StringComparison.OrdinalIgnoreCase))]);

    public void SetCapabilityState(string identity, DismComponentState state) =>
        _capabilities.Update(current => With(current, identity, state));

    public void SetFeatureState(string name, DismComponentState state) =>
        _features.Update(current => With(current, name, state));

    public void RemovePackage(string identity) =>
        _packages.Update(current =>
        {
            var copy = new Dictionary<string, DismComponentState>(current, StringComparer.OrdinalIgnoreCase);
            copy.Remove(identity);
            return copy;
        });

    /// <summary>
    /// Drops everything. Called after <c>/Cleanup-Image</c>, which can remove staged and superseded
    /// components behind our back and so invalidates every cached answer.
    /// </summary>
    public void InvalidateAll()
    {
        _appx.Invalidate();
        _capabilities.Invalidate();
        _features.Invalidate();
        _packages.Invalidate();
    }

    public void Dispose()
    {
        _appx.Dispose();
        _capabilities.Dispose();
        _features.Dispose();
        _packages.Dispose();
    }

    private static Dictionary<string, DismComponentState> With(
        IReadOnlyDictionary<string, DismComponentState> current, string key, DismComponentState state) =>
        new(current, StringComparer.OrdinalIgnoreCase)
        {
            [key] = state,
        };
}

/// <summary>A value loaded at most once, safely under concurrent callers.</summary>
internal sealed class AsyncCache<T>(Func<CancellationToken, Task<T>> loader) : IDisposable
    where T : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T? _value;

    public async Task<T> GetAsync(CancellationToken cancellationToken)
    {
        var cached = _value;
        if (cached is not null)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _value ??= await loader(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies a change locally. A no-op when nothing has been loaded — nothing to keep in sync.</summary>
    public void Update(Func<T, T> mutate)
    {
        var current = _value;
        if (current is not null)
        {
            _value = mutate(current);
        }
    }

    public void Invalidate() => _value = null;

    public void Dispose() => _gate.Dispose();
}
