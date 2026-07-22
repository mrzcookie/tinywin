using TinyWin.Catalog.Models;

namespace TinyWin.Catalog.Validation;

public sealed record CatalogError(string ComponentId, string Message)
{
    public override string ToString() => $"[{ComponentId}] {Message}";
}

/// <summary>
/// Whole-catalog invariants. Run as a unit test in CI — this is the thing that stops the catalog
/// rotting as entries are added. See docs/PLAN.md section 5.
/// </summary>
public static class CatalogValidator
{
    public static IReadOnlyList<CatalogError> Validate(CatalogDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var errors = new List<CatalogError>();
        var componentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presetIds = catalog.Presets
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var duplicate in catalog.Components
                     .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            errors.Add(new CatalogError(duplicate.Key, "Duplicate component id."));
        }

        foreach (var component in catalog.Components)
        {
            componentIds.Add(component.Id);

            if (component.Actions.Count == 0)
            {
                errors.Add(new CatalogError(component.Id, "Component has no actions."));
            }

            // Telling users what breaks is the product's main safety mechanism, so anything
            // that can hurt must say so. Safe components are allowed to stay silent.
            if (component.Risk > RiskTier.Safe && component.Breaks.Count == 0)
            {
                errors.Add(new CatalogError(
                    component.Id,
                    $"Risk tier '{component.Risk}' requires a non-empty 'breaks' list."));
            }

            if (component.AppliesTo.MinBuild is { } min && component.AppliesTo.MaxBuild is { } max && min > max)
            {
                errors.Add(new CatalogError(component.Id, $"appliesTo minBuild {min} exceeds maxBuild {max}."));
            }

            foreach (var preset in component.DefaultIn.Where(p => !presetIds.Contains(p)))
            {
                errors.Add(new CatalogError(component.Id, $"defaultIn references unknown preset '{preset}'."));
            }

            foreach (var preset in catalog.Presets.Where(p => component.DefaultIn.Contains(p.Id, StringComparer.OrdinalIgnoreCase)))
            {
                if (component.Risk > preset.MaxRisk)
                {
                    errors.Add(new CatalogError(
                        component.Id,
                        $"Risk tier '{component.Risk}' is not permitted in preset '{preset.Id}' (maxRisk '{preset.MaxRisk}')."));
                }
            }

            for (var i = 0; i < component.Actions.Count; i++)
            {
                foreach (var message in ActionValidator.Validate(component.Actions[i]))
                {
                    errors.Add(new CatalogError(component.Id, $"action[{i}]: {message}"));
                }
            }
        }

        // Cross-references resolve only once every id is known, so this is a second pass.
        foreach (var component in catalog.Components)
        {
            foreach (var required in component.Requires)
            {
                if (!componentIds.Contains(required))
                {
                    errors.Add(new CatalogError(component.Id, $"requires unknown component '{required}'."));
                }
            }

            foreach (var conflict in component.Conflicts)
            {
                if (!componentIds.Contains(conflict))
                {
                    errors.Add(new CatalogError(component.Id, $"conflicts with unknown component '{conflict}'."));
                }

                if (component.Requires.Contains(conflict, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(new CatalogError(component.Id, $"both requires and conflicts with '{conflict}'."));
                }
            }

            if (component.Requires.Contains(component.Id, StringComparer.OrdinalIgnoreCase) ||
                component.Conflicts.Contains(component.Id, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new CatalogError(component.Id, "Component references itself."));
            }
        }

        foreach (var preset in catalog.Presets)
        {
            foreach (var id in preset.Includes.Concat(preset.Excludes).Where(id => !componentIds.Contains(id)))
            {
                errors.Add(new CatalogError(preset.Id, $"Preset references unknown component '{id}'."));
            }
        }

        return errors;
    }
}
