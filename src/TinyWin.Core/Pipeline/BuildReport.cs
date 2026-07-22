using TinyWin.Core.Models;

namespace TinyWin.Core.Pipeline;

public sealed record StageReport
{
    public required BuildStageId Stage { get; init; }
    public required StageState State { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Error { get; init; }
}

public sealed record BuildReport
{
    public required bool Succeeded { get; init; }
    public required IReadOnlyList<StageReport> Stages { get; init; }
    public required IReadOnlyList<ActionOutcome> Actions { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public long SourceSizeBytes { get; init; }
    public long OutputSizeBytes { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public string? OutputIsoPath { get; init; }

    public int AppliedCount => Actions.Count(a => a.Status == ActionStatus.Applied);

    /// <summary>
    /// Actions whose target was not found. Surfaced prominently rather than buried: a high count
    /// means the catalog has drifted from the media, which is the failure this design exists to
    /// catch. See docs/PLAN.md section 2.1.
    /// </summary>
    public int NoTargetCount => Actions.Count(a => a.Status == ActionStatus.NoTarget);

    public int FailedCount => Actions.Count(a => a.Status == ActionStatus.Failed);
}
