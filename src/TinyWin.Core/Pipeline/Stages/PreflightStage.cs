using System.Security.Principal;
using TinyWin.Core.Abstractions;

namespace TinyWin.Core.Pipeline.Stages;

/// <summary>
/// Checks the machine can actually complete a build, and clears wreckage from a previous crash.
/// </summary>
/// <remarks>
/// Everything here fails fast and cheaply. A build that dies forty minutes in because the scratch
/// volume filled up, or because a crashed run left an image mounted, is the worst possible outcome
/// — all the cost, none of the result.
/// </remarks>
public sealed class PreflightStage(IImagingBackend backend, IOfflineRegistry registry) : IBuildStage
{
    /// <summary>Staging the ISO, exporting and recompressing all need room simultaneously.</summary>
    public const long RequiredFreeBytes = 25L * 1024 * 1024 * 1024;

    public BuildStageId Id => BuildStageId.Preflight;

    public string Title => "Checking prerequisites";

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        Report(progress, "Checking elevation");
        if (OperatingSystem.IsWindows() &&
            !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(
                "TinyWin must run as Administrator. DISM refuses to service an image otherwise (error 740).");
        }

        Report(progress, "Checking source media");
        if (!File.Exists(context.Request.SourceIsoPath))
        {
            throw new FileNotFoundException("Source ISO not found.", context.Request.SourceIsoPath);
        }

        Report(progress, "Checking free space");
        Directory.CreateDirectory(context.Request.ScratchDirectory);
        var root = Path.GetPathRoot(Path.GetFullPath(context.Request.ScratchDirectory));
        if (root is not null)
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < RequiredFreeBytes)
            {
                throw new IOException(
                    $"Need about {RequiredFreeBytes / (1024 * 1024 * 1024)} GB free on {root}, " +
                    $"but only {free / (1024 * 1024 * 1024)} GB is available.");
            }
        }

        // Recovery, not just validation. A previous crash leaves images mounted and hives loaded,
        // and neither clears itself — without this the machine cannot start another build until
        // it reboots.
        Report(progress, "Checking for leftovers from a previous run");

        var mounted = await backend.GetMountedImagesAsync(cancellationToken).ConfigureAwait(false);
        if (mounted.Count > 0)
        {
            context.Warn($"Found {mounted.Count} image(s) left mounted by a previous run; cleaning up.");
            await backend.CleanupMountPointsAsync(cancellationToken).ConfigureAwait(false);
        }

        var stranded = await registry.UnloadStrandedHivesAsync(cancellationToken).ConfigureAwait(false);
        if (stranded > 0)
        {
            context.Warn($"Unloaded {stranded} registry hive(s) stranded by a previous run.");
        }
    }

    private void Report(IProgress<BuildProgress> progress, string message) =>
        progress.Report(new BuildProgress { Stage = Id, State = StageState.Running, Message = message });
}
