using TinyWin.App.Services;
using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;

namespace TinyWin.App.Demo;

/// <summary>
/// A timed no-op stage. Reports progress the way a real stage would, and does nothing else.
/// </summary>
internal sealed class DemoBuildStage : IBuildStage
{
    private const int Steps = 20;

    public required BuildStageId Id { get; init; }

    public required string Title { get; init; }

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Optional per-stage applicability, mirroring the real skip conditions.</summary>
    public Func<BuildContext, bool>? Applies { get; init; }

    /// <summary>Detail line shown under the stage title as it runs.</summary>
    public Func<double, string>? Detail { get; init; }

    /// <summary>
    /// Set when the stage leaves the image mounted. The real MountImage stage must dismount with
    /// <c>/discard</c> when a run is cancelled; the demo models that so the unwind is visible in
    /// the log rather than merely asserted in a test.
    /// </summary>
    public bool HoldsMount { get; init; }

    public Action<string>? Log { get; init; }

    public bool ShouldRun(BuildContext context) => Applies?.Invoke(context) ?? true;

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        if (HoldsMount)
        {
            context.MountedImage = new MountedImage(
                context.InstallWimPath ?? "install.wim", context.Request.EditionIndex, "(scratch)\\mount");
        }

        for (var step = 1; step <= Steps; step++)
        {
            await Task.Delay(Duration / Steps, cancellationToken).ConfigureAwait(false);

            var percent = (double)step / Steps;
            progress.Report(new BuildProgress
            {
                Stage = Id,
                State = StageState.Running,
                StagePercent = percent,
                Message = Detail?.Invoke(percent) ?? Title,
            });
        }
    }

    public Task RollbackAsync(BuildContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Must be a no-op once the image has been committed, which is why this checks the context
        // rather than a flag of its own. See BuildPipeline's rollback contract.
        if (HoldsMount && context.MountedImage is not null)
        {
            Log?.Invoke($"Unwinding: dismounting {context.MountedImage.MountPath} with /discard");
            context.MountedImage = null;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Walks the resolved plan and records an outcome per action, so the Build log and the Done page
/// show the same per-action detail a real run would.
/// </summary>
internal sealed class DemoActionStage : IBuildStage
{
    public required BuildStageId Id { get; init; }

    public required string Title { get; init; }

    public required Func<BuildContext, IReadOnlyList<Catalog.Resolution.ResolvedAction>> Actions { get; init; }

    public TimeSpan PerAction { get; init; } = TimeSpan.FromMilliseconds(90);

    public Action<string>? Log { get; init; }

    public bool ShouldRun(BuildContext context) => Actions(context).Count > 0;

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var actions = Actions(context);
        var descriptions = actions
            .SelectMany(a => ActionDescriber.Describe(a.Action).Select(d => (a.ComponentId, Description: d, a.Action.Optional)))
            .ToList();

        for (var i = 0; i < descriptions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(PerAction, cancellationToken).ConfigureAwait(false);

            var (componentId, description, optional) = descriptions[i];

            // Deterministically pretend a few targets are missing. Not decoration: a build that
            // reports 100% applied every time would hide exactly the catalog drift that the
            // NoTarget status exists to expose (docs/PLAN.md section 2.1).
            var missing = optional && i % 7 == 3;

            var outcome = missing
                ? ActionOutcome.NoTarget(componentId, description)
                : ActionOutcome.Applied(componentId, description, PerAction);

            context.Record(outcome);
            Log?.Invoke(missing ? $"no target   {description}" : $"applied     {description}");

            progress.Report(new BuildProgress
            {
                Stage = Id,
                State = StageState.Running,
                StagePercent = (double)(i + 1) / descriptions.Count,
                Message = $"{description}  ({i + 1} of {descriptions.Count})",
            });
        }
    }
}

/// <summary>
/// Assembles the fourteen stages of docs/PLAN.md section 2.2 as demo stages.
/// </summary>
/// <remarks>
/// Replaced wholesale by the real stage list once M1 lands. The Build page knows nothing about this
/// type — it is handed an <see cref="IReadOnlyList{IBuildStage}"/> and runs it through the real
/// <see cref="BuildPipeline"/>, so ordering, skipping, progress and rollback are all exercised for
/// real even though the work is fake.
/// </remarks>
internal static class DemoPipelineFactory
{
    public static IReadOnlyList<IBuildStage> Create(Action<string> log) =>
    [
        new DemoBuildStage
        {
            Id = BuildStageId.Preflight,
            Title = ActionDescriber.StageTitle(BuildStageId.Preflight),
            Duration = TimeSpan.FromSeconds(1.2),
            Log = log,
            Detail = p => p < 0.5 ? "Checking free space on the scratch volume" : "Checking for stale DISM mounts",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.InspectIso,
            Title = ActionDescriber.StageTitle(BuildStageId.InspectIso),
            Duration = TimeSpan.FromSeconds(1.5),
            Log = log,
            Detail = _ => "Reading sources\\install.wim",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.StageFiles,
            Title = ActionDescriber.StageTitle(BuildStageId.StageFiles),
            Duration = TimeSpan.FromSeconds(4),
            Log = log,
            Detail = p => $"Copying ISO contents to scratch  ({p:P0})",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.NormalizeImage,
            Title = ActionDescriber.StageTitle(BuildStageId.NormalizeImage),
            Duration = TimeSpan.FromSeconds(3),
            Applies = c => c.ImageInfo?.IsEsd ?? false,
            Log = log,
            Detail = _ => "Exporting install.esd to install.wim",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.MountImage,
            Title = ActionDescriber.StageTitle(BuildStageId.MountImage),
            Duration = TimeSpan.FromSeconds(3),
            HoldsMount = true,
            Log = log,
            Detail = p => $"Mounting index {p:P0}",
        },
        new DemoActionStage
        {
            Id = BuildStageId.ApplyComponents,
            Title = ActionDescriber.StageTitle(BuildStageId.ApplyComponents),
            Actions = c => c.Plan.ImageActions,
            Log = log,
        },
        new DemoActionStage
        {
            Id = BuildStageId.ApplyRegistry,
            Title = ActionDescriber.StageTitle(BuildStageId.ApplyRegistry),
            Actions = c => c.Plan.RegistryActions,
            Log = log,
        },
        new DemoBuildStage
        {
            Id = BuildStageId.WriteUnattend,
            Title = ActionDescriber.StageTitle(BuildStageId.WriteUnattend),
            Duration = TimeSpan.FromSeconds(0.8),
            Log = log,
            Detail = _ => "Writing autounattend.xml to the ISO root",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.CleanupImage,
            Title = ActionDescriber.StageTitle(BuildStageId.CleanupImage),
            Duration = TimeSpan.FromSeconds(4),
            // Skipped when servicing itself has been stripped — there is no component store left to
            // clean. Keyed off the risk tier until the catalog carries an explicit WinSxS component.
            Applies = c => c.Request.CleanupComponentStore && c.Plan.HighestRisk != RiskTier.Unserviceable,
            Log = log,
            Detail = p => $"/StartComponentCleanup /ResetBase  ({p:P0})",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.CommitImage,
            Title = ActionDescriber.StageTitle(BuildStageId.CommitImage),
            Duration = TimeSpan.FromSeconds(3),
            Log = log,
            Detail = p => $"Dismount-WindowsImage -Save  ({p:P0})",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.RecompressImage,
            Title = ActionDescriber.StageTitle(BuildStageId.RecompressImage),
            Duration = TimeSpan.FromSeconds(5),
            Applies = c => c.Request.RecompressImage,
            Log = log,
            Detail = p => $"/Export-Image /Compress:recovery  ({p:P0})",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.PatchBootWim,
            Title = ActionDescriber.StageTitle(BuildStageId.PatchBootWim),
            Duration = TimeSpan.FromSeconds(1.5),
            Log = log,
            Detail = _ => "Applying setup bypasses to boot.wim index 2",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.BuildIso,
            Title = ActionDescriber.StageTitle(BuildStageId.BuildIso),
            Duration = TimeSpan.FromSeconds(4),
            Log = log,
            Detail = p => $"Writing dual BIOS/UEFI El Torito boot data  ({p:P0})",
        },
        new DemoBuildStage
        {
            Id = BuildStageId.Verify,
            Title = ActionDescriber.StageTitle(BuildStageId.Verify),
            Duration = TimeSpan.FromSeconds(1),
            Log = log,
            Detail = _ => "Comparing sizes and counting no-op actions",
        },
    ];
}
