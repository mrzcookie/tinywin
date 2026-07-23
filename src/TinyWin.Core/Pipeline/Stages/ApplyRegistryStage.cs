using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Core.Recovery;

namespace TinyWin.Core.Pipeline.Stages;

/// <summary>
/// Applies the registry half of the plan inside a single hive session.
/// </summary>
/// <remarks>
/// One session for every registry action, deliberately. Loading and unloading hives per action
/// would multiply the unload hazard in docs/PLAN.md section 3.3 by the number of actions, and
/// each unload is a chance to strand the image.
///
/// <see cref="ActionType.DisableService"/> lands here rather than in the imaging stage: a service
/// start type is just <c>SYSTEM\ControlSet001\Services\{name}\Start</c>, so it is a registry write
/// against an offline hive, not a DISM operation.
/// </remarks>
public sealed class ApplyRegistryStage(IOfflineRegistry registry) : IBuildStage
{
    public BuildStageId Id => BuildStageId.ApplyRegistry;

    public string Title => "Applying registry changes";

    /// <summary>Hive writes land inside the mount, so a discarded mount undoes all of them.</summary>
    public StageRecovery Recovery => StageRecovery.Volatile;

    public bool ShouldRun(BuildContext context) =>
        context is not null && (context.Plan.RegistryActions.Count > 0 || HasServiceActions(context));

    private static bool HasServiceActions(BuildContext context) =>
        context.Plan.ImageActions.Any(a => a.Action.Type == ActionType.DisableService);

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var image = context.MountedImage
            ?? throw new InvalidOperationException("No image is mounted.");

        // Service actions are rewritten as SYSTEM hive writes and folded in with the rest, so the
        // session covers everything in one open/close.
        var actions = context.Plan.RegistryActions
            .Concat(context.Plan.ImageActions
                .Where(a => a.Action.Type == ActionType.DisableService)
                .Select(a => a with { Action = ToServiceRegistryAction(a.Action) }))
            .ToList();

        var hives = actions
            .Select(a => a.Action.Hive)
            .OfType<RegistryHive>()
            .ToList();

        // A RemoveScheduledTask action carries no 'hive' field, deliberately — the registration is
        // always in SOFTWARE, so requiring authors to state it would be noise they could get wrong.
        // The stage supplies it instead. See docs/registry-findings.md section 2.
        if (actions.Any(a => a.Action.Type == ActionType.RemoveScheduledTask))
        {
            hives.Add(RegistryHive.Software);
        }

        hives = [.. hives.Distinct()];

        if (hives.Count == 0)
        {
            return;
        }

        await using var session = await registry
            .OpenAsync(image.MountPath, hives, cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < actions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolved = actions[i];
            var description = Describe(resolved.Action);

            try
            {
                var status = await session
                    .ApplyAsync(resolved.ComponentId, resolved.Action, cancellationToken)
                    .ConfigureAwait(false);

                context.Record(new ActionOutcome
                {
                    ComponentId = resolved.ComponentId,
                    Description = description,
                    Status = status,
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.Record(ActionOutcome.Failed(resolved.ComponentId, description, ex.Message));
            }

            progress.Report(new BuildProgress
            {
                Stage = Id,
                State = StageState.Running,
                StagePercent = (double)(i + 1) / actions.Count,
                Message = description,
            });
        }
    }

    private static ComponentAction ToServiceRegistryAction(ComponentAction action) => new()
    {
        Type = ActionType.SetRegistry,
        Hive = RegistryHive.System,
        Key = $@"ControlSet001\Services\{action.Name}",
        ValueName = "Start",
        Kind = RegistryValueKind.Dword,
        // 4 = disabled.
        Data = System.Text.Json.JsonDocument.Parse((action.StartType ?? 4).ToString()).RootElement,
        Optional = action.Optional,
    };

    private static string Describe(ComponentAction action) => action.Type switch
    {
        ActionType.SetRegistry => $"Set {action.Hive}\\{action.Key}\\{action.ValueName}",
        ActionType.DeleteRegistryKey => $"Delete {action.Hive}\\{action.Key}",
        ActionType.RemoveScheduledTask => $"Remove scheduled task {action.Name}",
        _ => $"{action.Type} {action.Key}",
    };
}
