namespace TinyWin.Core.Pipeline;

public enum StageState
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
}

/// <summary>The 14 stages of docs/PLAN.md section 2.2, in execution order.</summary>
public enum BuildStageId
{
    Preflight,
    InspectIso,
    StageFiles,
    NormalizeImage,
    MountImage,
    ApplyComponents,
    ApplyRegistry,
    WriteUnattend,
    CleanupImage,
    CommitImage,
    RecompressImage,
    PatchBootWim,
    BuildIso,
    Verify,
}

public sealed record BuildProgress
{
    public required BuildStageId Stage { get; init; }
    public required StageState State { get; init; }

    /// <summary>0.0 to 1.0 within the current stage, or null when indeterminate.</summary>
    public double? StagePercent { get; init; }

    public required string Message { get; init; }
    public TimeSpan Elapsed { get; init; }
}
