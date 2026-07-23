using TinyWin.Core.Pipeline;
using TinyWin.Core.Recovery;

namespace TinyWin.Tests;

/// <summary>
/// The resume arithmetic on its own, where every combination can be stated explicitly.
/// </summary>
/// <remarks>
/// The end-to-end tests in <see cref="BuildResumeTests"/> cover the two cases that actually happen;
/// these cover the ones that would be expensive to arrange but are one bad comparison away —
/// a sealing stage that did not complete, a checkpoint from a future version listing stages out of
/// order, a gap in the middle of the completed set.
/// </remarks>
public sealed class ResumePlanTests
{
    private static readonly StageRecoveryInfo[] Pipeline =
    [
        new(BuildStageId.Preflight, StageRecovery.AlwaysRerun),
        new(BuildStageId.StageFiles, StageRecovery.Durable),
        new(BuildStageId.InspectIso, StageRecovery.Durable),
        new(BuildStageId.MountImage, StageRecovery.Volatile),
        new(BuildStageId.ApplyComponents, StageRecovery.Volatile),
        new(BuildStageId.CommitImage, StageRecovery.Sealing),
        new(BuildStageId.RecompressImage, StageRecovery.Durable),
        new(BuildStageId.BuildIso, StageRecovery.Durable),
        new(BuildStageId.Verify, StageRecovery.AlwaysRerun),
    ];

    [Fact]
    public void Nothing_completed_resumes_from_the_first_real_stage()
    {
        var plan = ResumePlan.Compute(Pipeline, []);

        Assert.Equal(BuildStageId.StageFiles, plan.ResumeFrom);
        Assert.Empty(plan.Restored);
    }

    [Fact]
    public void An_uncommitted_mount_invalidates_everything_inside_it()
    {
        var plan = ResumePlan.Compute(Pipeline,
        [
            BuildStageId.Preflight,
            BuildStageId.StageFiles,
            BuildStageId.InspectIso,
            BuildStageId.MountImage,
            BuildStageId.ApplyComponents,
        ]);

        Assert.Equal(BuildStageId.MountImage, plan.ResumeFrom);
        Assert.Equal(2, plan.Restored.Count);
        Assert.Contains(BuildStageId.StageFiles, plan.Restored);
        Assert.Contains(BuildStageId.InspectIso, plan.Restored);
    }

    /// <summary>Once the commit lands, the volatile work is on disk and must not be redone.</summary>
    [Fact]
    public void A_completed_commit_makes_the_volatile_stages_reusable()
    {
        var plan = ResumePlan.Compute(Pipeline,
        [
            BuildStageId.Preflight,
            BuildStageId.StageFiles,
            BuildStageId.InspectIso,
            BuildStageId.MountImage,
            BuildStageId.ApplyComponents,
            BuildStageId.CommitImage,
            BuildStageId.RecompressImage,
        ]);

        Assert.Equal(BuildStageId.BuildIso, plan.ResumeFrom);
        Assert.Contains(BuildStageId.MountImage, plan.Restored);
        Assert.Contains(BuildStageId.ApplyComponents, plan.Restored);
    }

    /// <summary>
    /// Invalidation cascades forward: a gap early on means nothing after it can be trusted, even
    /// though those stages are recorded as complete.
    /// </summary>
    [Fact]
    public void A_gap_invalidates_every_later_stage()
    {
        var plan = ResumePlan.Compute(Pipeline,
        [
            BuildStageId.Preflight,
            BuildStageId.StageFiles,

            // InspectIso missing.
            BuildStageId.MountImage,
            BuildStageId.ApplyComponents,
            BuildStageId.CommitImage,
        ]);

        Assert.Equal(BuildStageId.InspectIso, plan.ResumeFrom);
        Assert.Equal([BuildStageId.StageFiles], plan.Restored);
    }

    /// <summary>
    /// Preflight and Verify produce nothing worth reusing, so they never appear as restored and
    /// never hold up the scan.
    /// </summary>
    [Fact]
    public void Always_rerun_stages_are_never_restored()
    {
        var plan = ResumePlan.Compute(Pipeline, [.. Pipeline.Select(s => s.Stage)]);

        Assert.Null(plan.ResumeFrom);
        Assert.DoesNotContain(BuildStageId.Preflight, plan.Restored);
        Assert.DoesNotContain(BuildStageId.Verify, plan.Restored);
        Assert.Contains(BuildStageId.BuildIso, plan.Restored);
    }

    [Fact]
    public void An_uncompleted_preflight_does_not_block_the_resume_point()
    {
        var plan = ResumePlan.Compute(Pipeline,
        [
            BuildStageId.StageFiles,
            BuildStageId.InspectIso,
        ]);

        Assert.Equal(BuildStageId.MountImage, plan.ResumeFrom);
        Assert.Contains(BuildStageId.StageFiles, plan.Restored);
    }

    /// <summary>A checkpoint listing stages this build does not have must not confuse the scan.</summary>
    [Fact]
    public void Unknown_completed_stages_are_ignored()
    {
        var plan = ResumePlan.Compute(Pipeline,
        [
            BuildStageId.StageFiles,
            BuildStageId.PatchBootWim,
            BuildStageId.WriteUnattend,
        ]);

        Assert.Equal(BuildStageId.InspectIso, plan.ResumeFrom);
        Assert.Equal([BuildStageId.StageFiles], plan.Restored);
    }
}
