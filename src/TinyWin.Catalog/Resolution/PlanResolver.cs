using TinyWin.Catalog.Models;

namespace TinyWin.Catalog.Resolution;

public sealed record ResolvedAction(string ComponentId, ComponentAction Action);

public sealed record ResolutionWarning(string ComponentId, string Message);

/// <summary>
/// A selection turned into an ordered, deduplicated, execution-ready action list.
/// </summary>
/// <remarks>
/// Image actions and registry actions are separated because they run in different pipeline stages:
/// registry work happens inside a single hive session (docs/PLAN.md section 3.3), and interleaving it
/// with DISM calls would mean loading and unloading hives repeatedly.
/// </remarks>
public sealed record ResolvedPlan
{
    public required IReadOnlyList<string> ComponentIds { get; init; }
    public required IReadOnlyList<ResolvedAction> ImageActions { get; init; }
    public required IReadOnlyList<ResolvedAction> RegistryActions { get; init; }
    public required IReadOnlyList<ResolutionWarning> Warnings { get; init; }
    public int EstimatedSavingsMb { get; init; }
    public RiskTier HighestRisk { get; init; }
}

public sealed class PlanResolutionException(IReadOnlyList<string> problems)
    : Exception("Cannot resolve selection: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

public static class PlanResolver
{
    /// <summary>
    /// Execution order within the image stage. Appx first because provisioned packages hold
    /// references to files a later delete would otherwise orphan; ownership changes last before
    /// deletes so a locked path can still be removed.
    /// </summary>
    private static readonly ActionType[] ImageActionOrder =
    [
        ActionType.RemoveProvisionedAppx,
        ActionType.RemoveCapability,
        ActionType.DisableFeature,
        ActionType.RemovePackage,
        ActionType.DisableService,
        ActionType.RemoveScheduledTask,
        ActionType.TakeOwnership,
        ActionType.DeleteFile,
        ActionType.DeleteDirectory,
    ];

    private static readonly ActionType[] RegistryActionTypes =
    [
        ActionType.SetRegistry,
        ActionType.DeleteRegistryKey,
    ];

    public static ResolvedPlan Resolve(
        CatalogDocument catalog,
        IEnumerable<string> selectedIds,
        int targetBuild)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selectedIds);

        var problems = new List<string>();
        var warnings = new List<ResolutionWarning>();
        var selected = new Dictionary<string, Component>(StringComparer.OrdinalIgnoreCase);

        // Transitive closure over 'requires'. A queue rather than recursion so a cyclic catalog
        // terminates instead of blowing the stack; the visited set makes cycles harmless.
        var queue = new Queue<string>(selectedIds);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (selected.ContainsKey(id))
            {
                continue;
            }

            var component = catalog.Find(id);
            if (component is null)
            {
                problems.Add($"Unknown component '{id}'.");
                continue;
            }

            selected[component.Id] = component;

            foreach (var required in component.Requires)
            {
                if (!selected.ContainsKey(required))
                {
                    queue.Enqueue(required);
                }
            }
        }

        foreach (var component in selected.Values)
        {
            foreach (var conflict in component.Conflicts.Where(selected.ContainsKey))
            {
                // Emit once per pair rather than twice.
                if (string.Compare(component.Id, conflict, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    problems.Add($"'{component.Id}' conflicts with '{conflict}'.");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new PlanResolutionException(problems);
        }

        foreach (var component in selected.Values.Where(c => !c.AppliesTo.Includes(targetBuild)))
        {
            // Not fatal by design: an unvalidated build should warn and report no-ops rather than
            // refuse outright. See docs/PLAN.md section 1 on 26H1.
            warnings.Add(new ResolutionWarning(
                component.Id,
                $"Not validated for build {targetBuild}; its actions may find no target."));
        }

        var ordered = selected.Values.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList();

        var imageActions = ImageActionOrder
            .SelectMany(type => ordered
                .SelectMany(c => c.Actions.Where(a => a.Type == type).Select(a => new ResolvedAction(c.Id, a))))
            .ToList();

        var registryActions = RegistryActionTypes
            .SelectMany(type => ordered
                .SelectMany(c => c.Actions.Where(a => a.Type == type).Select(a => new ResolvedAction(c.Id, a))))
            .ToList();

        return new ResolvedPlan
        {
            ComponentIds = [.. ordered.Select(c => c.Id)],
            ImageActions = imageActions,
            RegistryActions = registryActions,
            Warnings = warnings,
            EstimatedSavingsMb = ordered.Sum(c => c.EstimatedSavingsMb),
            HighestRisk = ordered.Count == 0 ? RiskTier.Safe : ordered.Max(c => c.Risk),
        };
    }

    /// <summary>Expands a preset into the component ids it selects.</summary>
    public static IReadOnlyList<string> ExpandPreset(CatalogDocument catalog, string presetId)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var preset = catalog.Presets.FirstOrDefault(p =>
            string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown preset '{presetId}'.", nameof(presetId));

        var excluded = preset.Excludes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. catalog.Components
                .Where(c => c.DefaultIn.Contains(preset.Id, StringComparer.OrdinalIgnoreCase))
                .Select(c => c.Id)
                .Concat(preset.Includes)
                .Where(id => !excluded.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
