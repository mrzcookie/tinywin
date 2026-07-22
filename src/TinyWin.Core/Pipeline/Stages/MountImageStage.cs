using TinyWin.Core.Abstractions;

namespace TinyWin.Core.Pipeline.Stages;

/// <summary>
/// Mounts the selected edition.
/// </summary>
/// <remarks>
/// The rollback is the important half. If anything downstream fails or the user cancels, the
/// image must be dismounted with <c>commit: false</c> — a mounted image left behind blocks every
/// future build and usually needs a reboot to clear.
/// </remarks>
public sealed class MountImageStage(IImagingBackend backend) : IBuildStage
{
    public BuildStageId Id => BuildStageId.MountImage;

    public string Title => "Mounting image";

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var wim = context.InstallWimPath
            ?? throw new InvalidOperationException("Install image path was not set by an earlier stage.");

        var mountPath = Path.Combine(context.Request.ScratchDirectory, "mount");
        Directory.CreateDirectory(mountPath);

        var relay = new Progress<double>(p => progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            StagePercent = p,
            Message = "Mounting image",
        }));

        context.MountedImage = await backend
            .MountAsync(wim, context.EditionIndex, mountPath, relay, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RollbackAsync(BuildContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.MountedImage is not { } image)
        {
            return;
        }

        // Discard, never commit. Rollback runs on the failure path, where committing would
        // persist a half-applied plan into the image.
        await backend.UnmountAsync(image, commit: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        context.MountedImage = null;
    }
}
