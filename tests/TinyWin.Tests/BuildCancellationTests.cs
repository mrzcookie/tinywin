using TinyWin.Core.Pipeline;
using TinyWin.Tests.Fakes;

namespace TinyWin.Tests;

/// <summary>
/// Cancellation unwinding, above the unit level.
/// </summary>
/// <remarks>
/// A cancelled build that strands a mounted image is the worst failure this product has: the image
/// stays mounted, the WIM stays locked, every later build fails at mount, and clearing it needs
/// <c>dism /Cleanup-Mountpoints</c> or a reboot. Each stage handles its own rollback correctly in
/// isolation; what these tests cover is the combination — cancel at an arbitrary point in the real
/// 14-stage pipeline and assert the machine is left clean.
/// </remarks>
public sealed class BuildCancellationTests
{
    [Fact]
    public async Task Cancelling_during_component_removal_dismounts_with_discard()
    {
        using var harness = new PipelineHarness();
        using var cts = new CancellationTokenSource();

        // Cancel the moment the first package removal is under way — mid-mount, mid-plan.
        harness.Imaging.OnCall = call =>
        {
            if (call.StartsWith("RemoveProvisionedAppx", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };

        var report = await harness.RunAsync(harness.Context(), cts.Token);

        Assert.False(report.Succeeded);
        Assert.True(report.Cancelled);

        var unmount = Assert.Single(harness.Imaging.Unmounts);
        Assert.False(unmount.Commit);
        Assert.Empty(harness.Imaging.Mounted);
    }

    /// <summary>
    /// The hive session must be disposed even when cancellation lands inside the registry stage —
    /// a loaded hive blocks the dismount that the same rollback is trying to perform.
    /// </summary>
    [Fact]
    public async Task Cancelling_during_registry_work_disposes_the_hive_session_and_dismounts()
    {
        using var harness = new PipelineHarness();
        using var cts = new CancellationTokenSource();

        harness.Registry.OnApply = () => cts.Cancel();

        var report = await harness.RunAsync(harness.Context(), cts.Token);

        Assert.True(report.Cancelled);
        Assert.NotEmpty(harness.Registry.Sessions);
        Assert.True(harness.Registry.AllSessionsDisposed);

        Assert.Empty(harness.Imaging.Mounted);
        Assert.All(harness.Imaging.Unmounts, u => Assert.False(u.Commit));
    }

    [Fact]
    public async Task Cancelling_before_the_mount_leaves_nothing_to_unmount()
    {
        using var harness = new PipelineHarness();
        using var cts = new CancellationTokenSource();

        harness.IsoBuilder.OnCall = call =>
        {
            if (call.StartsWith("Extract", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };

        var report = await harness.RunAsync(harness.Context(), cts.Token);

        Assert.True(report.Cancelled);
        Assert.Empty(harness.Imaging.Unmounts);
        Assert.Empty(harness.Imaging.Mounted);
    }

    /// <summary>
    /// A cancelled run is still resumable, which is the point of unwinding rather than aborting.
    /// </summary>
    [Fact]
    public async Task Cancelling_leaves_a_checkpoint_and_advice_naming_resume()
    {
        using var harness = new PipelineHarness();
        using var cts = new CancellationTokenSource();

        harness.Imaging.OnCall = call =>
        {
            if (call.StartsWith("Mount(", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };

        var report = await harness.RunAsync(harness.Context(), cts.Token);

        Assert.True(report.Cancelled);
        Assert.True(File.Exists(harness.CheckpointPath));

        var failure = Assert.Single(report.Stages, s => s.State == StageState.Failed);
        Assert.Contains("--resume", failure.Advice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure the rollback exists for: a dismount that itself fails must be reported loudly,
    /// with the command that clears it, rather than swallowed into a generic build failure.
    /// </summary>
    [Fact]
    public async Task A_dismount_that_fails_during_rollback_is_surfaced_as_a_warning()
    {
        using var harness = new PipelineHarness();
        using var cts = new CancellationTokenSource();

        harness.Imaging.UnmountFailure = new IOException(
            "The image could not be dismounted because a registry hive is still loaded.");

        harness.Imaging.OnCall = call =>
        {
            if (call.StartsWith("RemoveProvisionedAppx", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };

        var report = await harness.RunAsync(harness.Context(), cts.Token);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Warnings, w => w.Contains("Rollback of", StringComparison.Ordinal));
    }
}
