using TinyWin.Catalog.Models;
using TinyWin.Catalog.Resolution;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;
using TinyWin.Core.Pipeline.Stages;
using TinyWin.Tests.Fakes;

namespace TinyWin.Tests;

public sealed class ApplyComponentsStageTests : IDisposable
{
    private readonly string _mount = Path.Combine(Path.GetTempPath(), "tinywin-test-" + Guid.NewGuid().ToString("N"));

    public ApplyComponentsStageTests() => Directory.CreateDirectory(_mount);

    public void Dispose()
    {
        if (Directory.Exists(_mount))
        {
            Directory.Delete(_mount, recursive: true);
        }
    }

    private BuildContext Context(params ResolvedAction[] actions) => new()
    {
        Request = new BuildRequest
        {
            SourceIsoPath = "in.iso",
            OutputIsoPath = "out.iso",
            EditionIndex = 1,
            ComponentIds = [],
            ScratchDirectory = _mount,
        },
        Plan = new ResolvedPlan
        {
            ComponentIds = [],
            ImageActions = actions,
            RegistryActions = [],
            Warnings = [],
        },
        MountedImage = new Core.Abstractions.MountedImage("install.wim", 1, _mount),
    };

    private static async Task RunAsync(ApplyComponentsStage stage, BuildContext context) =>
        await stage.ExecuteAsync(context, new Progress<BuildProgress>(), TestContext.Current.CancellationToken);

    /// <summary>
    /// One outcome per package, not per action — otherwise a seven-package Xbox bundle where
    /// three are missing collapses into a single ambiguous result.
    /// </summary>
    [Fact]
    public async Task Appx_removal_records_one_outcome_per_package()
    {
        var backend = new FakeImagingBackend();
        backend.PresentPackages.Add("Microsoft.XboxApp");
        backend.PresentPackages.Add("Microsoft.GamingApp");

        var context = Context(new ResolvedAction("apps.xbox", new ComponentAction
        {
            Type = ActionType.RemoveProvisionedAppx,
            Packages = ["Microsoft.XboxApp", "Microsoft.GamingApp", "Microsoft.NotHere"],
        }));

        await RunAsync(new ApplyComponentsStage(backend), context);

        Assert.Equal(3, context.Outcomes.Count);
        Assert.Equal(2, context.Outcomes.Count(o => o.Status == ActionStatus.Applied));

        var missing = Assert.Single(context.Outcomes, o => o.Status == ActionStatus.NoTarget);
        Assert.Contains("Microsoft.NotHere", missing.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_file_is_reported_as_no_target_not_success()
    {
        var backend = new FakeImagingBackend();
        var context = Context(new ResolvedAction("apps.onedrive", new ComponentAction
        {
            Type = ActionType.DeleteFile,
            Path = @"Windows\System32\OneDriveSetup.exe",
        }));

        await RunAsync(new ApplyComponentsStage(backend), context);

        Assert.Equal(ActionStatus.NoTarget, Assert.Single(context.Outcomes).Status);
    }

    [Fact]
    public async Task Existing_file_is_deleted_and_reported_applied()
    {
        var backend = new FakeImagingBackend();
        var target = Path.Combine(_mount, "Windows", "System32");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(
            Path.Combine(target, "OneDriveSetup.exe"), "stub", TestContext.Current.CancellationToken);

        var context = Context(new ResolvedAction("apps.onedrive", new ComponentAction
        {
            Type = ActionType.DeleteFile,
            Path = @"Windows\System32\OneDriveSetup.exe",
        }));

        await RunAsync(new ApplyComponentsStage(backend), context);

        Assert.Equal(ActionStatus.Applied, Assert.Single(context.Outcomes).Status);
        Assert.False(File.Exists(Path.Combine(target, "OneDriveSetup.exe")));
    }

    /// <summary>
    /// Defence in depth against a catalog loaded outside the validator: a traversing path must
    /// never reach the host filesystem.
    /// </summary>
    [Fact]
    public async Task Path_escaping_the_mount_root_is_refused()
    {
        var backend = new FakeImagingBackend();
        var context = Context(new ResolvedAction("evil", new ComponentAction
        {
            Type = ActionType.DeleteDirectory,
            Path = @"..\..\Windows",
        }));

        await RunAsync(new ApplyComponentsStage(backend), context);

        var outcome = Assert.Single(context.Outcomes);
        Assert.Equal(ActionStatus.Failed, outcome.Status);
        Assert.Contains("escapes", outcome.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A single failed action must not throw away the whole run.</summary>
    [Fact]
    public async Task A_failed_action_does_not_abort_the_remaining_actions()
    {
        var backend = new FakeImagingBackend();
        backend.PresentPackages.Add("Microsoft.XboxApp");

        var context = Context(
            new ResolvedAction("evil", new ComponentAction { Type = ActionType.DeleteDirectory, Path = @"..\escape" }),
            new ResolvedAction("apps.xbox", new ComponentAction
            {
                Type = ActionType.RemoveProvisionedAppx,
                Packages = ["Microsoft.XboxApp"],
            }));

        await RunAsync(new ApplyComponentsStage(backend), context);

        Assert.Equal(2, context.Outcomes.Count);
        Assert.Contains(context.Outcomes, o => o.Status == ActionStatus.Failed);
        Assert.Contains(context.Outcomes, o => o.Status == ActionStatus.Applied);
    }

    [Fact]
    public async Task Stage_is_skipped_when_the_plan_has_no_image_actions()
    {
        var backend = new FakeImagingBackend();
        var stage = new ApplyComponentsStage(backend);

        Assert.False(stage.ShouldRun(Context()));
        await Task.CompletedTask;
    }

    /// <summary>Cancellation must surface, not be swallowed into a Failed outcome.</summary>
    [Fact]
    public async Task Cancellation_propagates()
    {
        var backend = new FakeImagingBackend();
        backend.PresentPackages.Add("Microsoft.XboxApp");

        var context = Context(new ResolvedAction("apps.xbox", new ComponentAction
        {
            Type = ActionType.RemoveProvisionedAppx,
            Packages = ["Microsoft.XboxApp"],
        }));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ApplyComponentsStage(backend).ExecuteAsync(context, new Progress<BuildProgress>(), cts.Token));
    }
}
