using System.Globalization;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;

namespace TinyWin.App.Demo;

/// <summary>
/// A stand-in <see cref="IImagingBackend"/> so the UI is demonstrable before M1 lands.
/// </summary>
/// <remarks>
/// This exists only because the real backend does not yet. It touches no image and shells out to
/// nothing — it reports the source file's real size and synthesises a plausible edition list.
/// Delete this class the moment <c>DismExeBackend</c> is available; the view models bind to
/// <see cref="IImagingBackend"/> and will not notice the swap.
///
/// Everything below <see cref="GetEditionsAsync"/> throws rather than pretending: the UI has no
/// business mounting anything, and a silent no-op here would be exactly the kind of lie the
/// no-op reporting rule in docs/PLAN.md section 2.1 exists to prevent.
/// </remarks>
internal sealed class DemoImagingBackend : IImagingBackend
{
    private const string NotImplementedMessage =
        "The imaging backend is not implemented yet (M1). The UI is running against demo data.";

    public async Task<IReadOnlyList<ImageEdition>> GetEditionsAsync(
        string wimPath, CancellationToken cancellationToken = default)
    {
        // Real inspection mounts the ISO read-only and reads sources\install.wim. Here the only real
        // input is the file itself, which at least keeps the size readout honest.
        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);

        var length = new FileInfo(wimPath).Length;
        var version = new Version(10, 0, 26100, 1742);

        // Rough per-edition share of the media, largest editions last, summing to the real file size.
        var weights = new[] { 0.97, 1.00, 1.02, 1.03 };
        var names = new[]
        {
            ("Windows 11 Home", "Home", "Windows 11 Home"),
            ("Windows 11 Pro", "Professional", "Windows 11 Pro"),
            ("Windows 11 Education", "Education", "Windows 11 Education"),
            ("Windows 11 Enterprise", "Enterprise", "Windows 11 Enterprise"),
        };

        return
        [
            .. names.Select((n, i) => new ImageEdition
            {
                Index = i + 1,
                Name = n.Item1,
                Description = n.Item3,
                EditionId = n.Item2,
                Architecture = "x64",
                Version = version,
                SizeBytes = (long)(length * weights[i] * 0.82),
                DefaultLanguage = CultureInfo.CurrentUICulture.Name,
            }),
        ];
    }

    public Task<MountedImage> MountAsync(
        string wimPath, int index, string mountPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplementedMessage);

    public Task UnmountAsync(
        MountedImage image, bool commit,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplementedMessage);

    public Task<IReadOnlyList<MountedImage>> GetMountedImagesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MountedImage>>([]);

    public Task CleanupMountPointsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(
        MountedImage image, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplementedMessage);

    public Task<ActionStatus> RemoveProvisionedAppxAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplementedMessage);

    public Task<ActionStatus> RemoveCapabilityAsync(
        MountedImage image, string capabilityName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplementedMessage);

    public Task<ActionStatus> DisableFeatureAsync(
        MountedImage image, string featureName, bool removePayload, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplementedMessage);

    public Task<ActionStatus> RemovePackageAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplementedMessage);

    public Task CleanupImageAsync(
        MountedImage image, bool resetBase,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplementedMessage);

    public Task ExportImageAsync(
        string sourceWimPath, int sourceIndex, string destinationWimPath, CompressionType compression,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplementedMessage);
}
