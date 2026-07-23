using TinyWin.Core.Abstractions;
using TinyWin.Core.Diagnostics;
using TinyWin.Core.Pipeline.Stages;
using TinyWin.Core.Recovery;

namespace TinyWin.Core.Pipeline;

/// <summary>
/// Assembles the 14 stages of docs/PLAN.md section 2.2 in execution order.
/// </summary>
/// <remarks>
/// Order is the contract here, not a detail. It is expressed once, in one list, so that the UI
/// and the CLI cannot drift into running different pipelines — which is the whole reason
/// TinyWin.Cli exists as a second head on the same Core.
///
/// Staging runs before inspection, which is the one deliberate deviation from section 2.2's
/// numbering. Microsoft's media hides everything in a UDF tree only Windows' own reader can open
/// (docs/findings/iso-builder.md section 1.3), so inspecting the ISO in place would mean mounting
/// it a second time; inspecting the staged copy reuses the mount extraction already performs.
/// See docs/findings/hardening.md section 5.
/// </remarks>
public static class BuildPipelineFactory
{
    /// <summary>Builds the pipeline.</summary>
    /// <param name="imaging">DISM backend.</param>
    /// <param name="registry">Offline hive engine.</param>
    /// <param name="isoBuilder">ISO extract and rebuild.</param>
    /// <param name="unattend">autounattend.xml generator.</param>
    /// <param name="scratchDirectory">
    /// Where <c>state.json</c> lives. Null disables checkpointing, which also disables
    /// <see cref="BuildRequest.Resume"/>; pass the request's scratch directory to enable both.
    /// </param>
    /// <param name="environment">
    /// Elevation and free-space source. Defaults to the real machine; tests substitute it.
    /// </param>
    public static BuildPipeline Create(
        IImagingBackend imaging,
        IOfflineRegistry registry,
        IIsoBuilder isoBuilder,
        IUnattendGenerator unattend,
        string? scratchDirectory = null,
        IBuildEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(imaging);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(isoBuilder);
        ArgumentNullException.ThrowIfNull(unattend);

        var machine = environment ?? SystemBuildEnvironment.Instance;
        var checkpoints = scratchDirectory is null ? null : new JsonCheckpointStore(scratchDirectory);

        return new BuildPipeline(
        [
            new PreflightStage(imaging, registry, isoBuilder, machine),
            new StageFilesStage(isoBuilder, machine),
            new InspectIsoStage(imaging, isoBuilder),
            new NormalizeImageStage(imaging, machine),
            new MountImageStage(imaging, machine),
            new ApplyComponentsStage(imaging),
            new ApplyRegistryStage(registry),
            new WriteUnattendStage(unattend),
            new CleanupImageStage(imaging, machine),
            new CommitImageStage(imaging),
            new RecompressImageStage(imaging, machine),
            new PatchBootWimStage(imaging, registry),
            new BuildIsoStage(isoBuilder, machine),
            new VerifyStage(),
        ],
        checkpoints);
    }
}
