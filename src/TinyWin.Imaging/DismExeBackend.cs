using System.Collections.Concurrent;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Imaging.Dism;
using TinyWin.Imaging.Execution;

namespace TinyWin.Imaging;

/// <summary>
/// <see cref="IImagingBackend"/> implemented by invoking <c>dism.exe</c>.
/// </summary>
/// <remarks>
/// <para><b>This backend is permanent, not a stopgap.</b> The spike checked the entire export table
/// of <c>dismapi.dll</c>: neither <c>/Cleanup-Image /StartComponentCleanup /ResetBase</c> (pipeline
/// stage 9) nor <c>/Export-Image</c> (stage 11) exists as any export. A future
/// <c>ManagedDismBackend</c> over the <c>Microsoft.Dism</c> package can take over mount, appx,
/// capabilities, features and packages, but <see cref="CleanupImageAsync"/> and
/// <see cref="ExportImageAsync"/> come back here. See docs/spikes/dism-backend.md §1 and §3.</para>
///
/// <para><b>Elevation.</b> DISM performs its elevation check before any work, so every method here
/// throws <see cref="DismElevationRequiredException"/> when the process is not elevated — including
/// the read-only <see cref="GetEditionsAsync"/>.</para>
///
/// <para><b>Absence is data.</b> Removal methods enumerate the image first and return
/// <see cref="ActionStatus.NoTarget"/> when the target is not present, rather than firing a removal
/// and interpreting whatever error comes back. The error codes are still mapped as a backstop, but
/// they are the second line of defence. Enumerations are cached per mounted image
/// (<see cref="ImageInventory"/>) so this costs four extra DISM invocations per image, not one per
/// action.</para>
///
/// <para><b>Cancellation.</b> Every long operation kills <c>dism.exe</c> and its <c>DismHost.exe</c>
/// child on cancellation, leaving the image mounted and dismountable so the caller can unwind with
/// <see cref="UnmountAsync"/> and <c>commit: false</c>. <see cref="UnmountAsync"/> itself is the one
/// exception and does not observe cancellation at all — see its remarks.</para>
/// </remarks>
public sealed class DismExeBackend : IImagingBackend, IDisposable
{
    private readonly IProcessRunner _runner;
    private readonly DismOptions _options;
    private readonly ConcurrentDictionary<MountedImage, ImageInventory> _inventories = new();

    public DismExeBackend()
        : this(new ChildProcessRunner(), DismOptions.Default)
    {
    }

    public DismExeBackend(DismOptions options)
        : this(new ChildProcessRunner(), options)
    {
    }

    public DismExeBackend(IProcessRunner runner, DismOptions options)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        _runner = runner;
        _options = options;
    }

    /// <summary>
    /// Receives every line DISM prints, as it prints it — the Build page's live log pane.
    /// </summary>
    /// <remarks>
    /// This is also what stage-transition progress is built on. When DISM emits no percentages,
    /// these lines are the only evidence the operation is moving at all.
    /// </remarks>
    public IProgress<string>? Log { get; set; }

    /// <summary>
    /// Enumerates editions with one <c>/Get-WimInfo</c> for the index list, then one per index for
    /// the detail. The list form does not report architecture, version or edition id, and those are
    /// required to classify the media, so the extra calls are not optional.
    /// </summary>
    public async Task<IReadOnlyList<ImageEdition>> GetEditionsAsync(
        string wimPath, CancellationToken cancellationToken = default)
    {
        var listing = await RunAsync(
            DismCommandLine.GetWimInfo(wimPath, _options), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var editions = new List<ImageEdition>();
        foreach (var summary in DismOutputParser.ParseWimIndexes(listing.Output))
        {
            var detail = await RunAsync(
                DismCommandLine.GetWimInfo(wimPath, summary.Index, _options), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var edition = DismOutputParser.ParseWimImageDetail(detail.Output);
            if (edition is not null)
            {
                editions.Add(edition);
            }
            else
            {
                // Fall back to the listing row rather than dropping the edition silently. A missing
                // edition would look to the user like media that does not contain what it contains.
                editions.Add(new ImageEdition
                {
                    Index = summary.Index,
                    Name = summary.Name,
                    Description = summary.Description,
                    EditionId = string.Empty,
                    Architecture = string.Empty,
                    Version = new Version(0, 0),
                    SizeBytes = summary.SizeBytes,
                });
            }
        }

        return editions;
    }

    public async Task<MountedImage> MountAsync(
        string wimPath, int index, string mountPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await RunAsync(
            DismCommandLine.MountWim(wimPath, index, mountPath, _options), progress, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(1.0);
        return new MountedImage(wimPath, index, mountPath);
    }

    /// <summary>
    /// Unmounts, committing or discarding.
    /// </summary>
    /// <remarks>
    /// <b>Cancellation is deliberately not honoured here.</b> Killing DISM part-way through writing
    /// a WIM back is the corrupted-image failure the rest of this class works to avoid, and it is
    /// not recoverable by <c>/Cleanup-Mountpoints</c>. The unwind path is to call this method with
    /// <paramref name="commit"/> set to false, which is exactly what a cancelled build does — so
    /// honouring the token here would abort the very call that performs the unwind.
    /// </remarks>
    public async Task UnmountAsync(
        MountedImage image, bool commit,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        try
        {
            await RunUninterruptibleAsync(DismCommandLine.UnmountWim(image.MountPath, commit, _options), progress)
                .ConfigureAwait(false);

            progress?.Report(1.0);
        }
        finally
        {
            if (_inventories.TryRemove(image, out var inventory))
            {
                inventory.Dispose();
            }
        }
    }

    public async Task<IReadOnlyList<MountedImage>> GetMountedImagesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            DismCommandLine.GetMountedWimInfo(_options), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return DismOutputParser.ParseMountedImages(result.Output);
    }

    public async Task CleanupMountPointsAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(
            DismCommandLine.CleanupMountpoints(_options), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        foreach (var inventory in _inventories.Values)
        {
            inventory.InvalidateAll();
        }
    }

    public Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(
        MountedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return GetInventory(image).GetAppxAsync(cancellationToken);
    }

    /// <summary>
    /// Removes a provisioned package, resolving a bare family name to the full package name DISM
    /// requires.
    /// </summary>
    /// <remarks>
    /// Catalogs name packages as <c>Microsoft.BingWeather</c>, but
    /// <c>/Remove-ProvisionedAppxPackage</c> only accepts the full
    /// <c>Microsoft.BingWeather_4.53.51361.0_neutral_~_8wekyb3d8bbwe</c>, which embeds a version
    /// that changes between builds. Resolving through the enumeration keeps the catalog free of
    /// version strings that would rot on every servicing update.
    /// </remarks>
    public async Task<ActionStatus> RemoveProvisionedAppxAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var inventory = GetInventory(image);
        var installed = await inventory.GetAppxAsync(cancellationToken).ConfigureAwait(false);

        var match = installed.FirstOrDefault(p => MatchesAppx(p, packageName));
        if (match is null)
        {
            return ActionStatus.NoTarget;
        }

        var result = await RunAllowingMissingTargetAsync(
            DismCommandLine.RemoveProvisionedAppxPackage(image.MountPath, match.PackageName, _options),
            cancellationToken)
            .ConfigureAwait(false);

        if (result == ActionStatus.Applied)
        {
            inventory.RemoveAppx(match.PackageName);
        }

        return result;
    }

    public async Task<ActionStatus> RemoveCapabilityAsync(
        MountedImage image, string capabilityName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var inventory = GetInventory(image);
        var capabilities = await inventory.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

        if (!TryResolveIdentity(capabilities, capabilityName, out var identity, out var state) ||
            state is not (DismComponentState.Installed or DismComponentState.Other))
        {
            return ActionStatus.NoTarget;
        }

        var result = await RunAllowingMissingTargetAsync(
            DismCommandLine.RemoveCapability(image.MountPath, identity, _options), cancellationToken)
            .ConfigureAwait(false);

        if (result == ActionStatus.Applied)
        {
            inventory.SetCapabilityState(identity, DismComponentState.Absent);
        }

        return result;
    }

    /// <summary>
    /// Disables a feature, optionally removing its payload.
    /// </summary>
    /// <remarks>
    /// The three-state distinction matters for honest reporting. A feature already
    /// <c>Disabled with Payload Removed</c> is a genuine no-target. A feature that is
    /// <c>Disabled</c> but still carries its payload is <i>not</i> — running with
    /// <paramref name="removePayload"/> still reclaims space, so that is real work and reports as
    /// applied.
    /// </remarks>
    public async Task<ActionStatus> DisableFeatureAsync(
        MountedImage image, string featureName, bool removePayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var inventory = GetInventory(image);
        var features = await inventory.GetFeaturesAsync(cancellationToken).ConfigureAwait(false);

        if (!features.TryGetValue(featureName, out var state))
        {
            return ActionStatus.NoTarget;
        }

        var alreadyDone = state switch
        {
            DismComponentState.DisabledWithPayloadRemoved => true,
            DismComponentState.Disabled => !removePayload,
            _ => false,
        };

        if (alreadyDone)
        {
            return ActionStatus.NoTarget;
        }

        var result = await RunAllowingMissingTargetAsync(
            DismCommandLine.DisableFeature(image.MountPath, featureName, removePayload, _options),
            cancellationToken)
            .ConfigureAwait(false);

        if (result == ActionStatus.Applied)
        {
            inventory.SetFeatureState(
                featureName,
                removePayload ? DismComponentState.DisabledWithPayloadRemoved : DismComponentState.Disabled);
        }

        return result;
    }

    public async Task<ActionStatus> RemovePackageAsync(
        MountedImage image, string packageName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var inventory = GetInventory(image);
        var packages = await inventory.GetPackagesAsync(cancellationToken).ConfigureAwait(false);

        if (!TryResolveIdentity(packages, packageName, out var identity, out var state) ||
            state == DismComponentState.Absent)
        {
            return ActionStatus.NoTarget;
        }

        var result = await RunAllowingMissingTargetAsync(
            DismCommandLine.RemovePackage(image.MountPath, identity, _options), cancellationToken)
            .ConfigureAwait(false);

        if (result == ActionStatus.Applied)
        {
            inventory.RemovePackage(identity);
        }

        return result;
    }

    /// <summary>
    /// <c>/Cleanup-Image /StartComponentCleanup</c>, optionally with <c>/ResetBase</c>.
    /// </summary>
    /// <remarks>
    /// One of the two operations with no DISM API equivalent, so this will run through
    /// <c>dism.exe</c> for as long as TinyWin exists (docs/spikes/dism-backend.md §3). It is also
    /// the longest single stage of a build — tens of minutes is normal — which is why progress
    /// matters here more than anywhere else.
    /// </remarks>
    public async Task CleanupImageAsync(
        MountedImage image, bool resetBase,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        await RunAsync(
            DismCommandLine.CleanupImage(image.MountPath, resetBase, _options), progress, cancellationToken)
            .ConfigureAwait(false);

        // Cleanup removes superseded and staged components, so every cached answer is now suspect.
        if (_inventories.TryGetValue(image, out var inventory))
        {
            inventory.InvalidateAll();
        }

        progress?.Report(1.0);
    }

    /// <summary>
    /// Re-exports an image to recompress it.
    /// </summary>
    /// <remarks>The other operation with no DISM API equivalent. See <see cref="CleanupImageAsync"/>.</remarks>
    public async Task ExportImageAsync(
        string sourceWimPath, int sourceIndex, string destinationWimPath, CompressionType compression,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await RunAsync(
            DismCommandLine.ExportImage(sourceWimPath, sourceIndex, destinationWimPath, compression, _options),
            progress,
            cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(1.0);
    }

    public void Dispose()
    {
        foreach (var inventory in _inventories.Values)
        {
            inventory.Dispose();
        }

        _inventories.Clear();
    }

    /// <summary>
    /// Matches a catalog name against a real package. Accepts the full package name, the family
    /// prefix before the first underscore, or the display name.
    /// </summary>
    internal static bool MatchesAppx(ProvisionedAppx package, string requested)
    {
        if (string.Equals(package.PackageName, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(package.DisplayName, requested, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "Microsoft.BingWeather" against "Microsoft.BingWeather_4.53.51361.0_neutral_~_8wekyb3d8bbwe".
        // The underscore is required so "Microsoft.Bing" cannot match "Microsoft.BingWeather_...".
        return package.PackageName.Length > requested.Length
            && package.PackageName.StartsWith(requested, StringComparison.OrdinalIgnoreCase)
            && package.PackageName[requested.Length] == '_';
    }

    /// <summary>
    /// Resolves a catalog name to the identity DISM listed, allowing the version suffix to be
    /// omitted. Capability and package identities embed a version
    /// (<c>Browser.InternetExplorer~~~~0.0.11.0</c>) that differs between builds, so a catalog
    /// pinning the full string would break on every servicing update.
    /// </summary>
    internal static bool TryResolveIdentity(
        IReadOnlyDictionary<string, DismComponentState> known,
        string requested,
        out string identity,
        out DismComponentState state)
    {
        if (known.TryGetValue(requested, out state))
        {
            identity = requested;
            return true;
        }

        var prefix = requested + "~";
        foreach (var candidate in known)
        {
            if (candidate.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                identity = candidate.Key;
                state = candidate.Value;
                return true;
            }
        }

        identity = requested;
        state = DismComponentState.Absent;
        return false;
    }

    private ImageInventory GetInventory(MountedImage image) =>
        _inventories.GetOrAdd(
            image,
            key => new ImageInventory(
                async ct => DismOutputParser.ParseProvisionedAppx(
                    (await RunAsync(DismCommandLine.GetProvisionedAppxPackages(key.MountPath, _options),
                        cancellationToken: ct).ConfigureAwait(false)).Output),
                async ct => DismOutputParser.ParseCapabilities(
                    (await RunAsync(DismCommandLine.GetCapabilities(key.MountPath, _options),
                        cancellationToken: ct).ConfigureAwait(false)).Output),
                async ct => DismOutputParser.ParseFeatures(
                    (await RunAsync(DismCommandLine.GetFeatures(key.MountPath, _options),
                        cancellationToken: ct).ConfigureAwait(false)).Output),
                async ct => DismOutputParser.ParsePackages(
                    (await RunAsync(DismCommandLine.GetPackages(key.MountPath, _options),
                        cancellationToken: ct).ConfigureAwait(false)).Output)));

    /// <summary>
    /// Runs a removal whose target we believe is present, tolerating a "not found" from DISM.
    /// </summary>
    /// <remarks>
    /// The enumeration is the primary source of truth, so reaching a missing-target error here means
    /// the enumeration and the removal disagreed. That is still a no-op rather than a failure, and
    /// reporting it as one keeps the build report honest.
    /// </remarks>
    private async Task<ActionStatus> RunAllowingMissingTargetAsync(
        string arguments, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(arguments, progress: null, killOnCancel: true, cancellationToken)
            .ConfigureAwait(false);

        if (DismExitCode.IsSuccess(result.ExitCode))
        {
            return ActionStatus.Applied;
        }

        if (DismExitCode.IsMissingTarget(result.ExitCode))
        {
            return ActionStatus.NoTarget;
        }

        throw DismException.ForExitCode(result.ExitCode, arguments, result.Output);
    }

    private async Task<ProcessRunResult> RunAsync(
        string arguments,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(arguments, progress, killOnCancel: true, cancellationToken)
            .ConfigureAwait(false);

        return Verify(arguments, result);
    }

    /// <summary>
    /// Runs a command that must not be interrupted, and therefore takes no
    /// <see cref="CancellationToken"/> at all. Only <see cref="UnmountAsync"/> uses this.
    /// </summary>
    private async Task<ProcessRunResult> RunUninterruptibleAsync(string arguments, IProgress<double>? progress)
    {
        var result = await ExecuteAsync(arguments, progress, killOnCancel: false, CancellationToken.None)
            .ConfigureAwait(false);

        return Verify(arguments, result);
    }

    private static ProcessRunResult Verify(string arguments, ProcessRunResult result) =>
        DismExitCode.IsSuccess(result.ExitCode)
            ? result
            : throw DismException.ForExitCode(result.ExitCode, arguments, result.Output);

    private async Task<ProcessRunResult> ExecuteAsync(
        string arguments,
        IProgress<double>? progress,
        bool killOnCancel,
        CancellationToken cancellationToken)
    {
        var stage = new DismStageProgress(progress);
        stage.Start();

        var log = Log;
        var request = new ProcessRunRequest(
            _options.ExecutablePath,
            arguments,
            Progress: new DelegateProgress<double>(stage.ReportPercentage),
            Log: new DelegateProgress<string>(line =>
            {
                stage.ReportStage();
                log?.Report(line);
            }),
            KillOnCancel: killOnCancel);

        return await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A synchronous <see cref="IProgress{T}"/>.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Progress{T}"/>: that posts to the captured synchronization context, which
    /// reorders reports relative to the output stream and, in a console host with no context, runs
    /// them on the thread pool. Progress here must stay in lock step with the lines that produced it.
    /// </remarks>
    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
