using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Diagnostics;
using TinyWin.Core.Recovery;

namespace TinyWin.Core.Pipeline.Stages;

/// <summary>
/// Compacts the component store. Long-running, and skipped when the selection has already removed
/// the store — running <c>ResetBase</c> against a stripped image is pointless and slow.
/// </summary>
public sealed class CleanupImageStage(IImagingBackend backend, IBuildEnvironment environment) : IBuildStage
{
    public BuildStageId Id => BuildStageId.CleanupImage;

    public string Title => "Compacting component store";

    public StageRecovery Recovery => StageRecovery.Volatile;

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

        // ResetBase rewrites the component store in place and needs room for both copies of
        // everything it touches. It is also the longest stage, so failing it on space at the end
        // wastes the most time.
        DiskSpace.Require(
            environment,
            image.MountPath,
            Math.Max(DiskSpace.CleanupFloorBytes, DiskSpace.Scale(DiskSpace.FileLength(image.WimPath), DiskSpace.CleanupFraction)),
            "Compacting the component store");

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
///
/// This is the pipeline's one <see cref="StageRecovery.Sealing"/> stage. Completing it turns every
/// volatile stage before it durable — the removals are now in the WIM on disk — which is what lets
/// a run that dies during the recompress resume without re-stripping the image.
/// </remarks>
public sealed class CommitImageStage(IImagingBackend backend) : IBuildStage
{
    public BuildStageId Id => BuildStageId.CommitImage;

    public string Title => "Saving image";

    public StageRecovery Recovery => StageRecovery.Sealing;

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
public sealed class RecompressImageStage(IImagingBackend backend, IBuildEnvironment environment) : IBuildStage
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

        // The export writes install2.wim beside install.wim; both exist until the move. This is the
        // one guard with an exact number rather than an estimate.
        DiskSpace.Require(
            environment,
            destination,
            DiskSpace.Scale(DiskSpace.FileLength(source), DiskSpace.RecompressOverhead),
            "Recompressing the image");

        var relay = new Progress<double>(p => progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            StagePercent = p,
            Message = "Recompressing image",
        }));

        await backend
            .ExportImageAsync(source, context.EditionIndex, destination, CompressionType.Recovery, relay, cancellationToken)
            .ConfigureAwait(false);

        File.Delete(source);
        File.Move(destination, source);
    }
}

/// <summary>Packages the staged tree into a bootable ISO.</summary>
public sealed class BuildIsoStage(IIsoBuilder isoBuilder, IBuildEnvironment environment) : IBuildStage
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

        // The output usually lands on a different volume from the scratch directory — next to the
        // source ISO — so preflight's scratch check says nothing about whether it fits.
        DiskSpace.Require(
            environment,
            context.Request.OutputIsoPath,
            DiskSpace.Scale(DiskSpace.MeasureDirectory(staged), DiskSpace.IsoOverhead),
            "Writing the ISO");

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
