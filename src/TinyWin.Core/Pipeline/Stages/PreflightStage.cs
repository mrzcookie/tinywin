using TinyWin.Core.Abstractions;
using TinyWin.Core.Diagnostics;
using TinyWin.Core.Recovery;

namespace TinyWin.Core.Pipeline.Stages;

/// <summary>
/// Checks the machine can actually complete a build, and clears wreckage from a previous crash.
/// </summary>
/// <remarks>
/// Everything here fails fast and cheaply. A build that dies forty minutes in because the scratch
/// volume filled up, or because a crashed run left an image mounted, is the worst possible outcome
/// — all the cost, none of the result.
///
/// This stage re-runs on a resumed build rather than being skipped: its job is to describe the
/// machine as it is now, and the leftovers it cleans up are usually the wreckage of the very run
/// being resumed.
/// </remarks>
public sealed class PreflightStage(
    IImagingBackend backend,
    IOfflineRegistry registry,
    IIsoBuilder isoBuilder,
    IBuildEnvironment environment) : IBuildStage
{
    /// <summary>Staging the ISO, exporting and recompressing all need room simultaneously.</summary>
    public const long RequiredFreeBytes = 25L * 1024 * 1024 * 1024;

    public BuildStageId Id => BuildStageId.Preflight;

    public string Title => "Checking prerequisites";

    public StageRecovery Recovery => StageRecovery.AlwaysRerun;

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        Report(progress, "Checking elevation");
        if (!environment.IsElevated)
        {
            throw new InvalidOperationException(
                "TinyWin must run as Administrator. DISM refuses to service an image otherwise, failing " +
                "with error 740 before it does any work — even for read-only queries. Close TinyWin, " +
                "right-click it and choose 'Run as administrator'.");
        }

        Report(progress, "Checking source media");
        if (!File.Exists(context.Request.SourceIsoPath))
        {
            throw new FileNotFoundException(
                $"Source ISO not found at '{context.Request.SourceIsoPath}'. Check the path — TinyWin " +
                "never downloads Windows, so the file has to exist before a build can start.",
                context.Request.SourceIsoPath);
        }

        Report(progress, "Checking the ISO writer");
        await CheckIsoBackendsAsync(context, cancellationToken).ConfigureAwait(false);

        Report(progress, "Checking free space");
        CheckFreeSpace(context);

        // Recovery, not just validation. A previous crash leaves images mounted and hives loaded,
        // and neither clears itself — without this the machine cannot start another build until
        // it reboots.
        Report(progress, "Checking for leftovers from a previous run");

        var mounted = await backend.GetMountedImagesAsync(cancellationToken).ConfigureAwait(false);
        if (mounted.Count > 0)
        {
            context.Warn(
                $"Found {mounted.Count} image(s) left mounted by a previous run at " +
                $"{string.Join(", ", mounted.Select(m => m.MountPath))}; discarding them. If this build " +
                "then fails to mount, run 'dism /Cleanup-Mountpoints' from an elevated prompt.");

            await backend.CleanupMountPointsAsync(cancellationToken).ConfigureAwait(false);
        }

        var stranded = await registry.UnloadStrandedHivesAsync(cancellationToken).ConfigureAwait(false);
        if (stranded > 0)
        {
            context.Warn($"Unloaded {stranded} registry hive(s) stranded by a previous run.");
        }
    }

    /// <summary>
    /// Fails now, with the command that fixes it, rather than at stage 13 after an hour of work.
    /// </summary>
    /// <remarks>
    /// The vendored xorriso is fetched by script and deliberately not committed
    /// (docs/findings/iso-builder.md section 3), so "no backend" is the expected state of a fresh
    /// clone rather than an exotic failure.
    /// </remarks>
    private async Task CheckIsoBackendsAsync(BuildContext context, CancellationToken cancellationToken)
    {
        var backends = await isoBuilder.ProbeBackendsAsync(cancellationToken).ConfigureAwait(false);
        if (backends.Any(b => b.Available))
        {
            return;
        }

        var reasons = backends.Count == 0
            ? "no backends were reported"
            : string.Join("; ", backends.Select(b => $"{b.Kind}: {b.UnavailableReason ?? "unavailable"}"));

        throw new InvalidOperationException(
            $"No ISO writer is available ({reasons}), so the build could not produce an ISO at the end. " +
            "Run 'tools\\fetch-xorriso.ps1' from the repository root to download the bundled xorriso, " +
            "or install the Windows ADK Deployment Tools to use oscdimg instead.");
    }

    /// <summary>
    /// Checks the scratch volume for the whole build, and the output volume separately.
    /// </summary>
    /// <remarks>
    /// Separately because they are usually different volumes: the scratch directory is wherever
    /// there is room, and the output ISO is written next to the source. A 25 GB scratch check says
    /// nothing about whether the finished ISO will fit where it is going.
    /// </remarks>
    private void CheckFreeSpace(BuildContext context)
    {
        Directory.CreateDirectory(context.Request.ScratchDirectory);

        DiskSpace.Require(
            environment, context.Request.ScratchDirectory, RequiredFreeBytes, "A build");

        var output = Path.GetDirectoryName(Path.GetFullPath(context.Request.OutputIsoPath));
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        Directory.CreateDirectory(output);

        var sourceSize = new FileInfo(context.Request.SourceIsoPath).Length;
        DiskSpace.Require(
            environment, output, DiskSpace.Scale(sourceSize, DiskSpace.IsoOverhead), "The finished ISO");
    }

    private void Report(IProgress<BuildProgress> progress, string message) =>
        progress.Report(new BuildProgress { Stage = Id, State = StageState.Running, Message = message });
}
