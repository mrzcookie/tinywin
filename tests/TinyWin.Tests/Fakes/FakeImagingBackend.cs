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

    /// <summary>
    /// Runs on every recorded call, so a test can cancel at a chosen point in the pipeline.
    /// </summary>
    /// <remarks>
    /// Cancellation is only interesting mid-flight: a token cancelled before the run never reaches
    /// the mount, and the failure worth testing is the one that strands a mounted image.
    /// </remarks>
    public Action<string>? OnCall { get; set; }

    /// <summary>Images this backend has ever been asked to unmount, with the commit flag used.</summary>
    public List<(MountedImage Image, bool Commit)> Unmounts { get; } = [];

    public HashSet<string> PresentPackages { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> PresentCapabilities { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ImageEdition> Editions { get; } = [];

    public List<MountedImage> Mounted { get; } = [];

    /// <summary>Set to make the next mount throw, to exercise rollback.</summary>
    public Exception? MountFailure { get; set; }

    /// <summary>
    /// Set to make every unmount throw — the stuck-dismount case that leaves an image mounted.
    /// </summary>
    public Exception? UnmountFailure { get; set; }

    private void Record(string call)
    {
        _calls.Enqueue(call);
        OnCall?.Invoke(call);
    }

    public Task<IReadOnlyList<ImageEdition>> GetEditionsAsync(string wimPath, CancellationToken cancellationToken = default)
    {
        Record($"GetEditions({wimPath})");
        return Task.FromResult<IReadOnlyList<ImageEdition>>(Editions);
    }

    public Task<MountedImage> MountAsync(
        string wimPath, int index, string mountPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Record($"Mount({wimPath}#{index} -> {mountPath})");

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
        ArgumentNullException.ThrowIfNull(image);

        Record($"Unmount({image.MountPath}, commit: {commit})");
        Unmounts.Add((image, commit));

        if (UnmountFailure is not null)
        {
            return Task.FromException(UnmountFailure);
        }

        Mounted.Remove(image);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MountedImage>> GetMountedImagesAsync(CancellationToken cancellationToken = default)
    {
        Record("GetMountedImages()");
        return Task.FromResult<IReadOnlyList<MountedImage>>([.. Mounted]);
    }

    public Task CleanupMountPointsAsync(CancellationToken cancellationToken = default)
    {
        Record("CleanupMountPoints()");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(
        MountedImage image, CancellationToken cancellationToken = default)
    {
        Record("GetProvisionedAppx()");
        return Task.FromResult<IReadOnlyList<ProvisionedAppx>>(
            [.. PresentPackages.Select(p => new ProvisionedAppx(p, p, "1.0.0.0"))]);
    }

    public Task<ActionStatus> RemoveProvisionedAppxAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default)
    {
        Record($"RemoveProvisionedAppx({packageName})");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PresentPackages.Remove(packageName) ? ActionStatus.Applied : ActionStatus.NoTarget);
    }

    public Task<ActionStatus> RemoveCapabilityAsync(
        MountedImage image, string capabilityName, CancellationToken cancellationToken = default)
    {
        Record($"RemoveCapability({capabilityName})");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PresentCapabilities.Remove(capabilityName) ? ActionStatus.Applied : ActionStatus.NoTarget);
    }

    public Task<ActionStatus> DisableFeatureAsync(
        MountedImage image, string featureName, bool removePayload, CancellationToken cancellationToken = default)
    {
        Record($"DisableFeature({featureName}, removePayload: {removePayload})");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ActionStatus.Applied);
    }

    public Task<ActionStatus> RemovePackageAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default)
    {
        Record($"RemovePackage({packageName})");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PresentPackages.Remove(packageName) ? ActionStatus.Applied : ActionStatus.NoTarget);
    }

    public Task CleanupImageAsync(
        MountedImage image, bool resetBase,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Record($"CleanupImage(resetBase: {resetBase})");
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1.0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a real destination file, because <c>RecompressImageStage</c> deletes the source and
    /// moves this over it. A no-op fake would make that stage untestable end to end.
    /// </summary>
    public async Task ExportImageAsync(
        string sourceWimPath, int sourceIndex, string destinationWimPath, CompressionType compression,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Record($"ExportImage({sourceWimPath}#{sourceIndex} -> {destinationWimPath}, {compression})");
        cancellationToken.ThrowIfCancellationRequested();

        var length = File.Exists(sourceWimPath) ? new FileInfo(sourceWimPath).Length : 4096;
        await File.WriteAllBytesAsync(destinationWimPath, new byte[length], cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(1.0);
    }
}
