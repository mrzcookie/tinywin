using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;
using TinyWin.Core.Recovery;
using TinyWin.Tests.Fakes;

namespace TinyWin.Tests;

/// <summary>
/// Crash recovery end to end: a run dies, the next one picks up where it left off.
/// </summary>
/// <remarks>
/// The property under test is not "it skipped some stages" but "it skipped exactly the stages
/// whose work survived". Skipping too few wastes the ~8 GB copy that makes resuming worth having;
/// skipping too many ships an image that was never actually modified, which is worse than any
/// failure — see docs/PLAN.md section 2.2 and <see cref="StageRecovery"/>.
/// </remarks>
public sealed class BuildResumeTests
{
    [Fact]
    public async Task A_failed_run_writes_a_checkpoint_naming_the_stages_it_completed()
    {
        using var harness = new PipelineHarness();
        harness.IsoBuilder.BuildFailure = new IOException("xorriso died");

        var report = await harness.RunAsync(harness.Context(), TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);

        var checkpoint = await new JsonCheckpointStore(harness.ScratchDirectory)
            .LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(checkpoint);
        Assert.Contains(BuildStageId.StageFiles, checkpoint.CompletedStages);
        Assert.Contains(BuildStageId.CommitImage, checkpoint.CompletedStages);
        Assert.DoesNotContain(BuildStageId.BuildIso, checkpoint.CompletedStages);
        Assert.Equal(harness.SourceIsoPath, checkpoint.SourceIsoPath);
    }

    /// <summary>
    /// The headline case. A late failure means the image is already committed, so the resumed run
    /// must not re-copy the ISO and must not re-mount and re-strip an image that is already done.
    /// </summary>
    [Fact]
    public async Task Resuming_after_a_late_failure_skips_the_copy_and_the_mount()
    {
        using var harness = new PipelineHarness();
        harness.IsoBuilder.BuildFailure = new IOException("xorriso died");

        var first = await harness.RunAsync(harness.Context(), TestContext.Current.CancellationToken);
        Assert.False(first.Succeeded);

        var extractsBefore = harness.IsoBuilder.Calls.Count(c => c.StartsWith("Extract", StringComparison.Ordinal));
        var mountsBefore = harness.Imaging.Calls.Count(c => c.StartsWith("Mount(", StringComparison.Ordinal));

        harness.IsoBuilder.BuildFailure = null;
        var second = await harness.RunAsync(harness.Context(resume: true), TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);
        Assert.Equal(BuildStageId.BuildIso, second.ResumedFrom);

        // The expensive work is not repeated.
        Assert.Equal(
            extractsBefore,
            harness.IsoBuilder.Calls.Count(c => c.StartsWith("Extract", StringComparison.Ordinal)));
        Assert.Equal(
            mountsBefore,
            harness.Imaging.Calls.Count(c => c.StartsWith("Mount(", StringComparison.Ordinal)));

        Assert.Contains(second.Stages, s => s.Stage == BuildStageId.StageFiles && s.Restored);
        Assert.Contains(second.Stages, s => s.Stage == BuildStageId.ApplyComponents && s.Restored);
    }

    /// <summary>
    /// The mirror case, and the one a naive "skip what completed" implementation gets wrong: the
    /// mount was discarded, so everything that happened inside it has to happen again.
    /// </summary>
    [Fact]
    public async Task Resuming_after_a_discarded_mount_redoes_the_removals_but_not_the_copy()
    {
        using var harness = new PipelineHarness();
        using var cts = new CancellationTokenSource();

        harness.Registry.OnApply = () => cts.Cancel();

        var first = await harness.RunAsync(harness.Context(), cts.Token);
        Assert.True(first.Cancelled);

        var extractsBefore = harness.IsoBuilder.Calls.Count(c => c.StartsWith("Extract", StringComparison.Ordinal));
        var appxBefore = harness.Imaging.Calls.Count(c => c.StartsWith("RemoveProvisionedAppx", StringComparison.Ordinal));

        harness.Registry.OnApply = null;

        // The first run removed the packages from the fake's inventory; a real discarded mount puts
        // them back, so restore them to model what the resumed run actually faces.
        harness.Imaging.PresentPackages.Add("Microsoft.XboxApp");
        harness.Imaging.PresentPackages.Add("Microsoft.GamingApp");

        var second = await harness.RunAsync(harness.Context(resume: true), TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);
        Assert.Equal(BuildStageId.MountImage, second.ResumedFrom);

        // Copy reused...
        Assert.Equal(
            extractsBefore,
            harness.IsoBuilder.Calls.Count(c => c.StartsWith("Extract", StringComparison.Ordinal)));

        // ...removals redone, because the mount that held them was discarded.
        Assert.True(
            harness.Imaging.Calls.Count(c => c.StartsWith("RemoveProvisionedAppx", StringComparison.Ordinal))
            > appxBefore);
    }

    /// <summary>
    /// Outcomes from a redone stage must not be counted twice, or the no-op number that
    /// docs/PLAN.md section 2.1 treats as the catalog-health signal stops meaning anything.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_does_not_double_count_the_actions_it_redid()
    {
        using var harness = new PipelineHarness();
        using var cts = new CancellationTokenSource();

        harness.Registry.OnApply = () => cts.Cancel();
        await harness.RunAsync(harness.Context(), cts.Token);

        harness.Registry.OnApply = null;
        harness.Imaging.PresentPackages.Add("Microsoft.XboxApp");
        harness.Imaging.PresentPackages.Add("Microsoft.GamingApp");

        var second = await harness.RunAsync(harness.Context(resume: true), TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);

        // Three packages in the plan: two present, one absent. Exactly once each.
        Assert.Equal(3, second.Actions.Count(a => a.Description.StartsWith("Remove app package", StringComparison.Ordinal)));
        Assert.Equal(1, second.Actions.Count(a => a.Status == ActionStatus.NoTarget));
    }

    [Fact]
    public async Task A_successful_build_deletes_its_checkpoint()
    {
        using var harness = new PipelineHarness();

        var report = await harness.RunAsync(harness.Context(), TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.False(File.Exists(harness.CheckpointPath));
        Assert.Null(report.ResumedFrom);
    }

    [Fact]
    public async Task Resuming_without_a_checkpoint_says_so_instead_of_silently_starting_over()
    {
        using var harness = new PipelineHarness();

        var ex = await Assert.ThrowsAsync<BuildCheckpointException>(() =>
            harness.RunAsync(harness.Context(resume: true), TestContext.Current.CancellationToken));

        Assert.Contains("without --resume", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same scratch directory, different media. Resuming that would blend two images, so it is
    /// refused with the reason and the way out.
    /// </summary>
    [Fact]
    public async Task A_checkpoint_from_a_different_build_is_refused()
    {
        using var harness = new PipelineHarness();
        harness.IsoBuilder.BuildFailure = new IOException("xorriso died");
        await harness.RunAsync(harness.Context(), TestContext.Current.CancellationToken);

        var otherIso = Path.Combine(harness.Root, "Win11_24H2.iso");
        await File.WriteAllBytesAsync(otherIso, new byte[32 * 1024], TestContext.Current.CancellationToken);

        var context = new BuildContext
        {
            Request = harness.Request(resume: true) with { SourceIsoPath = otherIso },
            Plan = PipelineHarness.Plan(),
        };

        var ex = await Assert.ThrowsAsync<BuildCheckpointException>(() =>
            harness.RunAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("different build", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resumability must never be load-bearing enough to fail a build: an unwritable checkpoint is
    /// a warning, not an error.
    /// </summary>
    [Fact]
    public async Task An_unwritable_checkpoint_warns_but_does_not_fail_the_build()
    {
        using var harness = new PipelineHarness();

        Directory.CreateDirectory(harness.ScratchDirectory);

        // A directory where the file belongs makes every write fail the way a locked file would.
        Directory.CreateDirectory(harness.CheckpointPath);

        var report = await harness.RunAsync(harness.Context(), TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.Contains(report.Warnings, w => w.Contains("resume checkpoint", StringComparison.Ordinal));
    }
}
