using TinyWin.Catalog;
using TinyWin.Catalog.Models;
using TinyWin.Catalog.Validation;

namespace TinyWin.Tests;

/// <summary>
/// Runs the real shipped catalog through the validator. This is the CI guard that keeps the
/// catalog from rotting as entries are added — see docs/PLAN.md section 5.
/// </summary>
public sealed class CatalogValidationTests
{
    private static async Task<CatalogDocument> LoadAsync()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "catalog");
        Assert.True(Directory.Exists(dir), $"Catalog directory not found at '{dir}'.");
        return await new DirectoryCatalogProvider(dir).LoadAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shipped_catalog_has_no_validation_errors()
    {
        var catalog = await LoadAsync();
        var errors = CatalogValidator.Validate(catalog);

        Assert.True(
            errors.Count == 0,
            "Catalog validation failed:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(e => "  " + e)));
    }

    [Fact]
    public async Task Shipped_catalog_is_not_empty()
    {
        var catalog = await LoadAsync();
        Assert.NotEmpty(catalog.Components);
        Assert.NotEmpty(catalog.Presets);
    }

    [Fact]
    public async Task Every_preset_resolves_without_conflicts()
    {
        var catalog = await LoadAsync();

        foreach (var preset in catalog.Presets)
        {
            var ids = Catalog.Resolution.PlanResolver.ExpandPreset(catalog, preset.Id);
            var plan = Catalog.Resolution.PlanResolver.Resolve(catalog, ids, targetBuild: 26200);

            Assert.True(
                plan.HighestRisk <= preset.MaxRisk,
                $"Preset '{preset.Id}' resolves to risk '{plan.HighestRisk}' but declares maxRisk '{preset.MaxRisk}'.");
        }
    }

    /// <summary>
    /// Encodes the decision recorded in docs/PLAN.md section 8: WebView2 is opt-in only, because
    /// removing it silently breaks unrelated third-party apps.
    /// </summary>
    [Fact]
    public async Task WebView2_is_not_selected_by_any_preset()
    {
        var catalog = await LoadAsync();
        var webview = catalog.Find("apps.edge.webview2");
        Assert.NotNull(webview);
        Assert.Empty(webview.DefaultIn);

        foreach (var preset in catalog.Presets)
        {
            var ids = Catalog.Resolution.PlanResolver.ExpandPreset(catalog, preset.Id);
            Assert.DoesNotContain("apps.edge.webview2", ids, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Defender ships, but only in Core. See docs/PLAN.md section 8.</summary>
    [Fact]
    public async Task Defender_is_unserviceable_and_core_only()
    {
        var catalog = await LoadAsync();
        var defender = catalog.Find("system.defender");

        Assert.NotNull(defender);
        Assert.Equal(RiskTier.Unserviceable, defender.Risk);
        Assert.Equal(["core"], defender.DefaultIn);
        Assert.NotEmpty(defender.Breaks);
    }

    [Fact]
    public async Task Every_component_targets_the_supported_build_range()
    {
        var catalog = await LoadAsync();

        foreach (var component in catalog.Components)
        {
            Assert.True(
                component.AppliesTo.Includes(26200),
                $"'{component.Id}' does not apply to build 26200 (25H2), the primary target.");
        }
    }

    /// <summary>
    /// 24H2 and 25H2 are one servicing branch, so v1 pins every entry to 26100-26299. A component
    /// that quietly widens its own range is claiming validation it never had — see docs/PLAN.md
    /// section 2.1 and the provenance gap in docs/catalog-gaps.md.
    /// </summary>
    [Fact]
    public async Task Every_component_pins_the_24H2_25H2_servicing_branch()
    {
        var catalog = await LoadAsync();

        foreach (var component in catalog.Components)
        {
            Assert.True(
                component.AppliesTo is { MinBuild: 26100, MaxBuild: 26299 },
                $"'{component.Id}' declares appliesTo " +
                $"[{component.AppliesTo.MinBuild?.ToString() ?? "null"}, " +
                $"{component.AppliesTo.MaxBuild?.ToString() ?? "null"}] rather than [26100, 26299]. " +
                "Widening the range needs a validation pass against that build first.");
        }
    }

    /// <summary>
    /// The validator only checks that <c>breaks</c> is non-empty. This checks it says something:
    /// the whole safety mechanism is telling users what stops working, and "this feature is
    /// removed" tells them nothing they did not already know from the component's name.
    /// </summary>
    [Fact]
    public async Task Breaks_describe_a_consequence_rather_than_restating_the_removal()
    {
        var catalog = await LoadAsync();
        var filler = new[]
        {
            "this feature is removed", "the feature is removed", "is removed", "is disabled",
            "removed from the image", "no longer available", "not available",
        };

        foreach (var component in catalog.Components.Where(c => c.Risk > RiskTier.Safe))
        {
            foreach (var entry in component.Breaks)
            {
                Assert.True(
                    entry.Trim().Length >= 25,
                    $"'{component.Id}' has a breaks entry too short to be useful: \"{entry}\".");

                Assert.False(
                    filler.Any(f => string.Equals(entry.Trim().TrimEnd('.'), f, StringComparison.OrdinalIgnoreCase)),
                    $"'{component.Id}' has a filler breaks entry: \"{entry}\". " +
                    "Say what stops working, not that something was removed.");
            }
        }
    }

    /// <summary>
    /// Unserviceable means the image can never be serviced or repaired, so it belongs to Core and
    /// nothing else. The validator's maxRisk check would let it into a hypothetical future preset
    /// that raised its ceiling; this pins the intent from docs/PLAN.md section 8.
    /// </summary>
    [Fact]
    public async Task Unserviceable_components_are_core_only()
    {
        var catalog = await LoadAsync();
        var unserviceable = catalog.Components.Where(c => c.Risk == RiskTier.Unserviceable).ToList();

        Assert.NotEmpty(unserviceable);

        foreach (var component in unserviceable)
        {
            Assert.True(
                component.DefaultIn.Count == 0 || component.DefaultIn.SequenceEqual(["core"]),
                $"Unserviceable component '{component.Id}' is default in " +
                $"[{string.Join(", ", component.DefaultIn)}] rather than core alone.");
        }
    }

    /// <summary>
    /// Presets are an escalating ladder, so each one must be a superset of the one below it.
    /// A component that appears in "balanced" but not "aggressive" means someone edited one
    /// defaultIn list and forgot the others.
    /// </summary>
    [Fact]
    public async Task Each_preset_is_a_superset_of_the_one_below_it()
    {
        var catalog = await LoadAsync();
        var ladder = catalog.Presets.OrderBy(p => p.Order).ToList();

        for (var i = 1; i < ladder.Count; i++)
        {
            var lower = Catalog.Resolution.PlanResolver.ExpandPreset(catalog, ladder[i - 1].Id);
            var higher = Catalog.Resolution.PlanResolver
                .ExpandPreset(catalog, ladder[i].Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = lower.Where(id => !higher.Contains(id)).ToList();

            Assert.True(
                missing.Count == 0,
                $"Preset '{ladder[i].Id}' omits components selected by '{ladder[i - 1].Id}': " +
                string.Join(", ", missing));
        }
    }

    /// <summary>Categories drive the UI tree in docs/PLAN.md section 4, so they are a closed set.</summary>
    [Fact]
    public async Task Every_component_uses_a_known_category()
    {
        var catalog = await LoadAsync();
        string[] known =
        [
            "AI & Assistant", "Microsoft Apps", "Media & Casual", "Gaming", "Bing & Content",
            "Telemetry & Privacy", "Optional Features", "Language & Input", "System (caution)",
            "Unserviceable",
        ];

        foreach (var component in catalog.Components)
        {
            Assert.True(
                known.Contains(component.Category, StringComparer.Ordinal),
                $"'{component.Id}' uses unknown category '{component.Category}'.");
        }
    }

    /// <summary>
    /// Every component in the Unserviceable category carries the matching risk tier and vice
    /// versa. Category is what the UI groups by; risk is what gates the confirmation. They must
    /// not disagree.
    /// </summary>
    [Fact]
    public async Task Unserviceable_category_and_risk_tier_agree()
    {
        var catalog = await LoadAsync();

        foreach (var component in catalog.Components)
        {
            Assert.Equal(
                component.Category == "Unserviceable",
                component.Risk == RiskTier.Unserviceable);
        }
    }
}
