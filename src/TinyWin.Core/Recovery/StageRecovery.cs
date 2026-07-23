using TinyWin.Core.Pipeline;

namespace TinyWin.Core.Recovery;

/// <summary>
/// What a stage's completion is worth to a later run.
/// </summary>
/// <remarks>
/// Resumability is not "skip everything that finished". Half the pipeline writes into a mounted
/// image, and a build that failed or was cancelled dismounted that image with <c>/Discard</c> — so
/// those stages finished and their work is gone. Recording only "completed" and skipping on that
/// basis would resume straight past the removals into <c>Commit</c> and ship an unmodified image,
/// which is worse than not resuming at all.
/// </remarks>
public enum StageRecovery
{
    /// <summary>
    /// The work landed on disk and outlives the process — the staged ISO tree, the exported WIM,
    /// the written ISO. Skipped on resume. These are the expensive ones and the whole point.
    /// </summary>
    Durable,

    /// <summary>
    /// The work lives inside the mounted image and dies with the mount, unless a later
    /// <see cref="Sealing"/> stage committed it first.
    /// </summary>
    Volatile,

    /// <summary>
    /// Writes the mounted image's contents back to the WIM, making every earlier
    /// <see cref="Volatile"/> stage durable retroactively.
    /// </summary>
    Sealing,

    /// <summary>
    /// Produces nothing to reuse and is cheap, so it runs every time — preflight's leftover
    /// cleanup and the final verification both want to see the current state of the machine, not
    /// last run's.
    /// </summary>
    AlwaysRerun,
}

/// <summary>One stage's position and recovery class, which is all <see cref="ResumePlan"/> needs.</summary>
public readonly record struct StageRecoveryInfo(BuildStageId Stage, StageRecovery Recovery);

/// <summary>Which stages a resumed run may skip, and where it picks up.</summary>
public sealed record ResumePlan
{
    /// <summary>The first stage that must run again, or null when every stage is already done.</summary>
    public required BuildStageId? ResumeFrom { get; init; }

    /// <summary>Stages whose completed work is being reused rather than redone.</summary>
    public required IReadOnlySet<BuildStageId> Restored { get; init; }

    public static ResumePlan None { get; } = new()
    {
        ResumeFrom = null,
        Restored = new HashSet<BuildStageId>(),
    };

    /// <summary>
    /// Works out the resume point from the checkpoint's completed set.
    /// </summary>
    /// <remarks>
    /// The scan stops at the first stage that has to run again, and only stages strictly before
    /// that point are reused. Invalidation therefore cascades forward, which is the property that
    /// keeps a resumed run coherent: if the mount has to be redone, everything that happened inside
    /// it is redone too, rather than a committed image being re-mounted and re-stripped.
    /// </remarks>
    public static ResumePlan Compute(
        IReadOnlyList<StageRecoveryInfo> stages, IReadOnlyCollection<BuildStageId> completed)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(completed);

        var done = completed.ToHashSet();
        var resumeIndex = stages.Count;

        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];

            // These re-run wherever they sit and never gate the resume point.
            if (stage.Recovery == StageRecovery.AlwaysRerun)
            {
                continue;
            }

            if (!done.Contains(stage.Stage))
            {
                resumeIndex = i;
                break;
            }

            if (stage.Recovery == StageRecovery.Volatile && !SealedAfter(stages, done, i))
            {
                resumeIndex = i;
                break;
            }
        }

        var restored = new HashSet<BuildStageId>();
        for (var i = 0; i < resumeIndex; i++)
        {
            if (stages[i].Recovery != StageRecovery.AlwaysRerun && done.Contains(stages[i].Stage))
            {
                restored.Add(stages[i].Stage);
            }
        }

        return new ResumePlan
        {
            ResumeFrom = resumeIndex < stages.Count ? stages[resumeIndex].Stage : null,
            Restored = restored,
        };
    }

    /// <summary>Whether a completed sealing stage after <paramref name="index"/> preserved its work.</summary>
    private static bool SealedAfter(
        IReadOnlyList<StageRecoveryInfo> stages, HashSet<BuildStageId> done, int index)
    {
        for (var i = index + 1; i < stages.Count; i++)
        {
            if (stages[i].Recovery == StageRecovery.Sealing && done.Contains(stages[i].Stage))
            {
                return true;
            }
        }

        return false;
    }
}
