using TinyWin.Core.Models;

namespace TinyWin.Core.Pipeline;

public sealed record StageReport
{
    public required BuildStageId Stage { get; init; }
    public required StageState State { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Error { get; init; }

    /// <summary>What the user can do about <see cref="Error"/>. See <c>FailureAdvice</c>.</summary>
    public string? Advice { get; init; }

    /// <summary>
    /// Set when the stage was skipped because a checkpoint from an interrupted run already had it
    /// done — as opposed to skipped because the selection did not need it.
    /// </summary>
    public bool Restored { get; init; }
}

public sealed record BuildReport
{
    public required bool Succeeded { get; init; }
    public required IReadOnlyList<StageReport> Stages { get; init; }
    public required IReadOnlyList<ActionOutcome> Actions { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Distinguishes "the user stopped it" from "it broke". Both leave Succeeded false.</summary>
    public bool Cancelled { get; init; }

    public long SourceSizeBytes { get; init; }
    public long OutputSizeBytes { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public string? OutputIsoPath { get; init; }

    /// <summary>Where a resumed run picked up, or null when it ran from the beginning.</summary>
    public BuildStageId? ResumedFrom { get; init; }

    /// <summary>Stages whose work was reused from the checkpoint instead of redone.</summary>
    public IReadOnlyList<BuildStageId> RestoredStages { get; init; } = [];

    public int AppliedCount => Actions.Count(a => a.Status == ActionStatus.Applied);

    /// <summary>
    /// Actions whose target was not found. Surfaced prominently rather than buried: a high count
    /// means the catalog has drifted from the media, which is the failure this design exists to
    /// catch. See docs/PLAN.md section 2.1.
    /// </summary>
    public int NoTargetCount => Actions.Count(a => a.Status == ActionStatus.NoTarget);

    public int FailedCount => Actions.Count(a => a.Status == ActionStatus.Failed);

    public int SkippedCount => Actions.Count(a => a.Status == ActionStatus.Skipped);

    /// <summary>
    /// The share of actions that found nothing, which is the catalog-drift signal itself. A number
    /// rather than a count because "12 no-ops" means something very different in a 20-action
    /// minimal build than in a 340-action core build.
    /// </summary>
    public double NoTargetRatio => Actions.Count == 0 ? 0 : (double)NoTargetCount / Actions.Count;

    /// <summary>The first failed stage's error and advice, for a caller that wants one line.</summary>
    public StageReport? FailedStage => Stages.FirstOrDefault(s => s.State == StageState.Failed);
}
