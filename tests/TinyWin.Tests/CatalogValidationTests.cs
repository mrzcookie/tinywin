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
}
