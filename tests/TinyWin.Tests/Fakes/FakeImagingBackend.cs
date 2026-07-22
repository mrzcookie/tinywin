using System.Collections.Concurrent;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;

namespace TinyWin.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IImagingBackend"/> that records the calls made against it.
/// </summary>
/// <remarks>
/// This is what makes the pipeline testable without an ISO, elevation, or 40 minutes. Tests assert
/// on <see cref="Calls"/> — the sequence and ordering of operations — rather than on DISM itself.
/// Packages not in <see cref="PresentPackages"/> return <see cref="ActionStatus.NoTarget"/>, so the
/// no-op reporting path gets exercised too.
/// </remarks>
public sealed class FakeImagingBackend : IImagingBackend
{
    private readonly ConcurrentQueue<string> _calls = new();

    public IReadOnlyList<string> Calls => [.. _calls];

    public HashSet<string> PresentPackages { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> PresentCapabilities { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ImageEdition> Editions { get; } = [];

    public List<MountedImage> Mounted { get; } = [];

    /// <summary>Set to make the next mount throw, to exercise rollback.</summary>
    public Exception? MountFailure { get; set; }

    public Task<IReadOnlyList<ImageEdition>> GetEditionsAsync(string wimPath, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue($"GetEditions({wimPath})");
        return Task.FromResult<IReadOnlyList<ImageEdition>>(Editions);
    }

    public Task<MountedImage> MountAsync(
        string wimPath, int index, string mountPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue($"Mount({wimPath}#{index} -> {mountPath})");

        if (MountFailure is not null)
        {
            return Task.FromException<MountedImage>(MountFailure);
        }

        var image = new MountedImage(wimPath, index, mountPath);
        Mounted.Add(image);
        progress?.Report(1.0);
        return Task.FromResult(image);
    }

    public Task UnmountAsync(
        MountedImage image, bool commit,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue($"Unmount({image.MountPath}, commit: {commit})");
        Mounted.Remove(image);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MountedImage>> GetMountedImagesAsync(CancellationToken cancellationToken = default)
    {
        _calls.Enqueue("GetMountedImages()");
        return Task.FromResult<IReadOnlyList<MountedImage>>([.. Mounted]);
    }

    public Task CleanupMountPointsAsync(CancellationToken cancellationToken = default)
    {
        _calls.Enqueue("CleanupMountPoints()");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(
        MountedImage image, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue("GetProvisionedAppx()");
        return Task.FromResult<IReadOnlyList<ProvisionedAppx>>(
            [.. PresentPackages.Select(p => new ProvisionedAppx(p, p, "1.0.0.0"))]);
    }

    public Task<ActionStatus> RemoveProvisionedAppxAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue($"RemoveProvisionedAppx({packageName})");
        return Task.FromResult(PresentPackages.Remove(packageName) ? ActionStatus.Applied : ActionStatus.NoTarget);
    }

    public Task<ActionStatus> RemoveCapabilityAsync(
        MountedImage image, string capabilityName, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue($"RemoveCapability({capabilityName})");
        return Task.FromResult(PresentCapabilities.Remove(capabilityName) ? ActionStatus.Applied : ActionStatus.NoTarget);
    }

    public Task<ActionStatus> DisableFeatureAsync(
        MountedImage image, string featureName, bool removePayload, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue($"DisableFeature({featureName}, removePayload: {removePayload})");
        return Task.FromResult(ActionStatus.Applied);
    }

    public Task<ActionStatus> RemovePackageAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue($"RemovePackage({packageName})");
        return Task.FromResult(PresentPackages.Remove(packageName) ? ActionStatus.Applied : ActionStatus.NoTarget);
    }

    public Task CleanupImageAsync(
        MountedImage image, bool resetBase,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue($"CleanupImage(resetBase: {resetBase})");
        progress?.Report(1.0);
        return Task.CompletedTask;
    }

    public Task ExportImageAsync(
        string sourceWimPath, int sourceIndex, string destinationWimPath, CompressionType compression,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue($"ExportImage({sourceWimPath}#{sourceIndex} -> {destinationWimPath}, {compression})");
        progress?.Report(1.0);
        return Task.CompletedTask;
    }
}
