using TinyWin.Core.Abstractions;
using TinyWin.Core.Pipeline.Stages;

namespace TinyWin.Core.Pipeline;

/// <summary>
/// Assembles the 14 stages of docs/PLAN.md section 2.2 in execution order.
/// </summary>
/// <remarks>
/// Order is the contract here, not a detail. It is expressed once, in one list, so that the UI
/// and the CLI cannot drift into running different pipelines — which is the whole reason
/// TinyWin.Cli exists as a second head on the same Core.
/// </remarks>
public static class BuildPipelineFactory
{
    public static BuildPipeline Create(
        IImagingBackend imaging,
        IOfflineRegistry registry,
        IIsoBuilder isoBuilder,
        IUnattendGenerator unattend)
    {
        ArgumentNullException.ThrowIfNull(imaging);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(isoBuilder);
        ArgumentNullException.ThrowIfNull(unattend);

        return new BuildPipeline(
        [
            new PreflightStage(imaging, registry),
            new StageFilesStage(isoBuilder),
            new InspectIsoStage(imaging, isoBuilder),
            new NormalizeImageStage(imaging),
            new MountImageStage(imaging),
            new ApplyComponentsStage(imaging),
            new ApplyRegistryStage(registry),
            new WriteUnattendStage(unattend),
            new CleanupImageStage(imaging),
            new CommitImageStage(imaging),
            new RecompressImageStage(imaging),
            new BuildIsoStage(isoBuilder),
            new VerifyStage(),
        ]);
    }
}
