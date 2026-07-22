using System.Diagnostics;
using TinyWin.Catalog.Models;
using TinyWin.Catalog.Resolution;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;

namespace TinyWin.Core.Pipeline.Stages;

/// <summary>
/// Executes the image half of the resolved plan: appx, capabilities, features, packages,
/// services, scheduled tasks and file deletions, in the order the resolver fixed.
/// </summary>
/// <remarks>
/// Every action produces exactly one <see cref="ActionOutcome"/>, including the ones that find
/// nothing. That is the mechanism behind "12 of 340 actions found no target" in the build report,
/// and it is how catalog drift becomes visible instead of silent.
///
/// One failed action does not abort the build. A missing scheduled task should not throw away
/// forty minutes of work — it is recorded as <see cref="ActionStatus.Failed"/> and the run
/// continues. Genuinely fatal problems surface from the backend as exceptions.
/// </remarks>
public sealed class ApplyComponentsStage(IImagingBackend backend) : IBuildStage
{
    public BuildStageId Id => BuildStageId.ApplyComponents;

    public string Title => "Removing components";

    public bool ShouldRun(BuildContext context) =>
        context is not null && context.Plan.ImageActions.Count > 0;

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var image = context.MountedImage
            ?? throw new InvalidOperationException("No image is mounted.");

        var actions = context.Plan.ImageActions;

        // Appx removal has no native progress or cancellation granularity (see the DISM spike),
        // so per-item progress has to be emitted here. Without it the longest phase of a debloat
        // run shows nothing and looks hung.
        var total = actions.Sum(a => a.Action.Type == ActionType.RemoveProvisionedAppx
            ? Math.Max(a.Action.Packages.Count, 1)
            : 1);
        var done = 0;

        foreach (var resolved in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var outcome in await ExecuteActionAsync(image, resolved, cancellationToken).ConfigureAwait(false))
            {
                context.Record(outcome);
                done++;

                progress.Report(new BuildProgress
                {
                    Stage = Id,
                    State = StageState.Running,
                    StagePercent = total == 0 ? null : (double)done / total,
                    Message = outcome.Description,
                });
            }
        }
    }

    private async Task<IReadOnlyList<ActionOutcome>> ExecuteActionAsync(
        MountedImage image, ResolvedAction resolved, CancellationToken cancellationToken)
    {
        var action = resolved.Action;
        var component = resolved.ComponentId;
        var results = new List<ActionOutcome>();

        switch (action.Type)
        {
            case ActionType.RemoveProvisionedAppx:
                // One outcome per package, not per action — otherwise a bundle of seven Xbox
                // packages where three are missing reports as a single ambiguous result.
                foreach (var package in action.Packages)
                {
                    results.Add(await RunAsync(
                        component, $"Remove app package {package}",
                        () => backend.RemoveProvisionedAppxAsync(image, package, cancellationToken))
                        .ConfigureAwait(false));
                }

                break;

            case ActionType.RemoveCapability:
                results.Add(await RunAsync(
                    component, $"Remove capability {action.Name}",
                    () => backend.RemoveCapabilityAsync(image, action.Name!, cancellationToken))
                    .ConfigureAwait(false));
                break;

            case ActionType.DisableFeature:
                results.Add(await RunAsync(
                    component, $"Disable feature {action.Name}",
                    () => backend.DisableFeatureAsync(image, action.Name!, removePayload: true, cancellationToken))
                    .ConfigureAwait(false));
                break;

            case ActionType.RemovePackage:
                results.Add(await RunAsync(
                    component, $"Remove package {action.Name}",
                    () => backend.RemovePackageAsync(image, action.Name!, cancellationToken))
                    .ConfigureAwait(false));
                break;

            case ActionType.DeleteFile:
            case ActionType.DeleteDirectory:
                results.Add(DeletePath(image, resolved));
                break;

            case ActionType.RemoveScheduledTask:
                // Routed to ApplyRegistryStage by PlanResolver and should never arrive here.
                // Deleting the task file would be wrong anyway: an offline image ships only nine
                // task definitions, and the rest are materialised at setup from TaskCache in the
                // SOFTWARE hive. See docs/catalog-gaps.md section 3.1.
                results.Add(ActionOutcome.Failed(
                    component,
                    $"Remove scheduled task {action.Name}",
                    "Scheduled task actions must be routed to the registry stage."));
                break;

            case ActionType.DisableService:
            case ActionType.TakeOwnership:
                // DisableService is applied through the SYSTEM hive in ApplyRegistryStage;
                // TakeOwnership is a filesystem ACL operation the imaging backend does not model.
                // Both are recorded rather than silently dropped.
                results.Add(new ActionOutcome
                {
                    ComponentId = component,
                    Description = $"{action.Type} {action.Name ?? action.Path}",
                    Status = ActionStatus.Skipped,
                    Detail = "Handled by a later stage.",
                });
                break;

            default:
                results.Add(ActionOutcome.Failed(
                    component, action.Type.ToString(), $"No executor for action type '{action.Type}'."));
                break;
        }

        return results;
    }

    private static async Task<ActionOutcome> RunAsync(
        string componentId, string description, Func<Task<ActionStatus>> operation)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var status = await operation().ConfigureAwait(false);
            return new ActionOutcome
            {
                ComponentId = componentId,
                Description = description,
                Status = status,
                Duration = sw.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ActionOutcome.Failed(componentId, description, ex.Message);
        }
    }

    private static ActionOutcome DeletePath(MountedImage image, ResolvedAction resolved)
    {
        var action = resolved.Action;
        var description = $"Delete {action.Path}";

        // ActionValidator already rejects rooted and traversing paths, but combining and
        // re-checking here means a catalog loaded from outside the validator cannot escape
        // the mount root either.
        var full = Path.GetFullPath(Path.Combine(image.MountPath, action.Path!));
        var root = Path.GetFullPath(image.MountPath);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return ActionOutcome.Failed(
                resolved.ComponentId, description, $"Path escapes the mount root: '{action.Path}'.");
        }

        try
        {
            if (action.Type == ActionType.DeleteDirectory)
            {
                if (!Directory.Exists(full))
                {
                    return ActionOutcome.NoTarget(resolved.ComponentId, description);
                }

                Directory.Delete(full, recursive: true);
            }
            else
            {
                if (!File.Exists(full))
                {
                    return ActionOutcome.NoTarget(resolved.ComponentId, description);
                }

                File.Delete(full);
            }

            return ActionOutcome.Applied(resolved.ComponentId, description);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ActionOutcome.Failed(resolved.ComponentId, description, ex.Message);
        }
    }

}
