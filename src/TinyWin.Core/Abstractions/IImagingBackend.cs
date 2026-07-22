using TinyWin.Core.Models;

namespace TinyWin.Core.Abstractions;

/// <summary>Details of an image currently mounted by us.</summary>
public sealed record MountedImage(string WimPath, int Index, string MountPath);

public sealed record ProvisionedAppx(string PackageName, string DisplayName, string? Version);

/// <summary>
/// Everything TinyWin needs from DISM.
/// </summary>
/// <remarks>
/// This interface exists so the backend choice stays reversible — see docs/PLAN.md section 3.2.
/// Ship <c>DismExeBackend</c> (process invocation) as the guaranteed floor, then swap in native
/// implementations behind the same contract as a pure optimisation.
///
/// Implementations must be safe to call from a background thread and must honour cancellation by
/// leaving the image in a dismountable state — never a half-committed one.
/// </remarks>
public interface IImagingBackend
{
    Task<IReadOnlyList<ImageEdition>> GetEditionsAsync(
        string wimPath, CancellationToken cancellationToken = default);

    Task<MountedImage> MountAsync(
        string wimPath, int index, string mountPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Unmounts, either committing changes or discarding them. Cancellation must discard.</summary>
    Task UnmountAsync(
        MountedImage image, bool commit,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Images we have left mounted, including from a previous crashed run.</summary>
    Task<IReadOnlyList<MountedImage>> GetMountedImagesAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards orphaned mount points left by a crash. Part of preflight recovery.</summary>
    Task CleanupMountPointsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(
        MountedImage image, CancellationToken cancellationToken = default);

    /// <returns><see cref="ActionStatus.NoTarget"/> when the package is not present.</returns>
    Task<ActionStatus> RemoveProvisionedAppxAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default);

    Task<ActionStatus> RemoveCapabilityAsync(
        MountedImage image, string capabilityName, CancellationToken cancellationToken = default);

    Task<ActionStatus> DisableFeatureAsync(
        MountedImage image, string featureName, bool removePayload, CancellationToken cancellationToken = default);

    Task<ActionStatus> RemovePackageAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default);

    /// <summary>StartComponentCleanup with ResetBase. Long-running; report progress.</summary>
    Task CleanupImageAsync(
        MountedImage image, bool resetBase,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Re-exports an image to recompress it, typically with recovery compression.</summary>
    Task ExportImageAsync(
        string sourceWimPath, int sourceIndex, string destinationWimPath, CompressionType compression,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public enum CompressionType
{
    None,
    Fast,
    Maximum,
    Recovery,
}
