using TinyWin.Catalog.Models;
using TinyWin.Catalog.Resolution;
using TinyWin.Core.Pipeline;
using TinyWin.Tests.Fakes;

namespace TinyWin.Tests;

public sealed class BuildPipelineTests
{
    private sealed class TestStage(
        BuildStageId id,
        Func<BuildContext, Task>? execute = null,
        bool shouldRun = true) : IBuildStage
    {
        public BuildStageId Id => id;
        public string Title => id.ToString();
        public bool RolledBack { get; private set; }

        public bool ShouldRun(BuildContext context) => shouldRun;

        public Task ExecuteAsync(BuildContext context, IProgress<BuildProgress> progress, CancellationToken ct) =>
            execute?.Invoke(context) ?? Task.CompletedTask;

        public Task RollbackAsync(BuildContext context, CancellationToken ct)
        {
            RolledBack = true;
            return Task.CompletedTask;
        }
    }

    private static BuildContext Context() => new()
    {
        Request = new BuildRequest
        {
            SourceIsoPath = "in.iso",
            OutputIsoPath = "out.iso",
            EditionIndex = 1,
            ComponentIds = [],
            ScratchDirectory = "scratch",
        },
        Plan = new ResolvedPlan
        {
            ComponentIds = [],
            ImageActions = [],
            RegistryActions = [],
            Warnings = [],
        },
    };

    [Fact]
    public async Task Successful_run_reports_every_stage_completed()
    {
        var pipeline = new BuildPipeline(
        [
            new TestStage(BuildStageId.Preflight),
            new TestStage(BuildStageId.InspectIso),
        ]);

        var report = await pipeline.RunAsync(
            Context(), new Progress<BuildProgress>(), TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.All(report.Stages, s => Assert.Equal(StageState.Completed, s.State));
        Assert.Equal("out.iso", report.OutputIsoPath);
    }

    /// <summary>
    /// The behaviour that matters most: a mid-build failure must unwind every stage that started,
    /// in reverse order, so nothing is left mounted.
    /// </summary>
    [Fact]
    public async Task Failure_rolls_back_started_stages_in_reverse_order()
    {
        var first = new TestStage(BuildStageId.Preflight);
        var second = new TestStage(BuildStageId.MountImage);
        var failing = new TestStage(
            BuildStageId.ApplyComponents,
            _ => throw new InvalidOperationException("boom"));
        var never = new TestStage(BuildStageId.BuildIso);

        var report = await pipeline([first, second, failing, never]);

        Assert.False(report.Succeeded);
        Assert.True(first.RolledBack);
        Assert.True(second.RolledBack);
        Assert.True(failing.RolledBack);
        Assert.False(never.RolledBack);

        var failed = Assert.Single(report.Stages, s => s.State == StageState.Failed);
        Assert.Equal(BuildStageId.ApplyComponents, failed.Stage);
        Assert.Equal("boom", failed.Error);

        // The stage after the failure must never have run.
        Assert.DoesNotContain(report.Stages, s => s.Stage == BuildStageId.BuildIso);

        static async Task<BuildReport> pipeline(IBuildStage[] stages) =>
            await new BuildPipeline(stages).RunAsync(
                Context(), new Progress<BuildProgress>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Skipped_stages_are_reported_rather_than_omitted()
    {
        var pipeline = new BuildPipeline(
        [
            new TestStage(BuildStageId.Preflight),
            new TestStage(BuildStageId.CleanupImage, shouldRun: false),
        ]);

        var report = await pipeline.RunAsync(
            Context(), new Progress<BuildProgress>(), TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.Contains(report.Stages, s => s.Stage == BuildStageId.CleanupImage && s.State == StageState.Skipped);
    }

    /// <summary>A rollback that itself fails is recorded, but must not mask the original error.</summary>
    [Fact]
    public async Task Rollback_failure_is_recorded_as_a_warning()
    {
        var throwing = new ThrowingRollbackStage();
        var failing = new TestStage(BuildStageId.ApplyComponents, _ => throw new InvalidOperationException("original"));

        var context = Context();
        var report = await new BuildPipeline([throwing, failing]).RunAsync(
            context, new Progress<BuildProgress>(), TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.Equal("original", report.Stages.Single(s => s.State == StageState.Failed).Error);
        Assert.Contains(report.Warnings, w => w.Contains("Rollback", StringComparison.Ordinal));
    }

    private sealed class ThrowingRollbackStage : IBuildStage
    {
        public BuildStageId Id => BuildStageId.MountImage;
        public string Title => "Mount";

        public Task ExecuteAsync(BuildContext context, IProgress<BuildProgress> progress, CancellationToken ct) =>
            Task.CompletedTask;

        public Task RollbackAsync(BuildContext context, CancellationToken ct) =>
            throw new IOException("dismount failed");
    }

    [Fact]
    public async Task Fake_backend_reports_no_target_for_absent_packages()
    {
        var backend = new FakeImagingBackend();
        backend.PresentPackages.Add("Microsoft.XboxApp");

        var image = await backend.MountAsync("install.wim", 1, "mount", null, TestContext.Current.CancellationToken);

        Assert.Equal(
            Core.Models.ActionStatus.Applied,
            await backend.RemoveProvisionedAppxAsync(image, "Microsoft.XboxApp", TestContext.Current.CancellationToken));

        Assert.Equal(
            Core.Models.ActionStatus.NoTarget,
            await backend.RemoveProvisionedAppxAsync(image, "Microsoft.Nonexistent", TestContext.Current.CancellationToken));
    }
}
