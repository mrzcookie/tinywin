using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;

namespace TinyWin.Core.Pipeline.Stages;

/// <summary>
/// Compacts the component store. Long-running, and skipped when the selection has already removed
/// the store — running <c>ResetBase</c> against a stripped image is pointless and slow.
/// </summary>
public sealed class CleanupImageStage(IImagingBackend backend) : IBuildStage
{
    public BuildStageId Id => BuildStageId.CleanupImage;

    public string Title => "Compacting component store";

    public bool ShouldRun(BuildContext context)
    {
        if (context is null || !context.Request.CleanupComponentStore)
        {
            return false;
        }

        // An unserviceable selection has removed WinSxS, so there is nothing left to clean.
        return context.Plan.HighestRisk != RiskTier.Unserviceable;
    }

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var image = context.MountedImage ?? throw new InvalidOperationException("No image is mounted.");

        var relay = new Progress<double>(p => progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            StagePercent = p,
            Message = "Compacting component store (this takes a while)",
        }));

        await backend.CleanupImageAsync(image, resetBase: true, relay, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Commits and unmounts. After this succeeds the changes are in the WIM.
/// </summary>
/// <remarks>
/// <see cref="RollbackAsync"/> is intentionally a no-op: once committed there is nothing to
/// discard, and the earlier <see cref="MountImageStage"/> rollback would otherwise try to
/// dismount an image that is already gone.
/// </remarks>
public sealed class CommitImageStage(IImagingBackend backend) : IBuildStage
{
    public BuildStageId Id => BuildStageId.CommitImage;

    public string Title => "Saving image";

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var image = context.MountedImage ?? throw new InvalidOperationException("No image is mounted.");

        var relay = new Progress<double>(p => progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            StagePercent = p,
            Message = "Saving image",
        }));

        await backend.UnmountAsync(image, commit: true, relay, cancellationToken).ConfigureAwait(false);

        // Clear it so MountImageStage's rollback does not try to dismount it again.
        context.MountedImage = null;
    }
}

/// <summary>Re-exports the WIM with recovery compression. Most of the size win comes from here.</summary>
public sealed class RecompressImageStage(IImagingBackend backend) : IBuildStage
{
    public BuildStageId Id => BuildStageId.RecompressImage;

    public string Title => "Recompressing image";

    public bool ShouldRun(BuildContext context) => context?.Request.RecompressImage ?? false;

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var source = context.InstallWimPath
            ?? throw new InvalidOperationException("Install image path was not set by an earlier stage.");

        var destination = Path.Combine(Path.GetDirectoryName(source)!, "install2.wim");

        var relay = new Progress<double>(p => progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            StagePercent = p,
            Message = "Recompressing image",
        }));

        await backend
            .ExportImageAsync(source, context.Request.EditionIndex, destination, CompressionType.Recovery, relay, cancellationToken)
            .ConfigureAwait(false);

        File.Delete(source);
        File.Move(destination, source);
    }
}

/// <summary>Packages the staged tree into a bootable ISO.</summary>
public sealed class BuildIsoStage(IIsoBuilder isoBuilder) : IBuildStage
{
    public BuildStageId Id => BuildStageId.BuildIso;

    public string Title => "Building ISO";

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var staged = context.StagedIsoDirectory
            ?? throw new InvalidOperationException("Staged ISO directory was not set by an earlier stage.");

        var relay = new Progress<double>(p => progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            StagePercent = p,
            Message = "Writing ISO",
        }));

        await isoBuilder.BuildAsync(
            new IsoBuildRequest
            {
                SourceDirectory = staged,
                OutputIsoPath = context.Request.OutputIsoPath,
                // Read from the source during Inspect rather than guessed — xorriso silently
                // accepts a wrong boot load size. See docs/spikes/iso-build.md section 5.
                BootGeometry = context.BootGeometry,
            },
            relay,
            cancellationToken).ConfigureAwait(false);
    }
}
