using TinyWin.Catalog;
using TinyWin.Catalog.Models;
using TinyWin.Catalog.Resolution;

namespace TinyWin.Tests;

public sealed class PlanResolverTests
{
    private static Component Comp(
        string id,
        RiskTier risk = RiskTier.Safe,
        string[]? requires = null,
        string[]? conflicts = null,
        ComponentAction[]? actions = null,
        int minBuild = 26100,
        int maxBuild = 26299) => new()
        {
            Id = id,
            Name = id,
            Category = "Test",
            Description = "Test component.",
            Risk = risk,
            Breaks = risk > RiskTier.Safe ? ["something"] : [],
            Requires = requires ?? [],
            Conflicts = conflicts ?? [],
            AppliesTo = new BuildRange { MinBuild = minBuild, MaxBuild = maxBuild },
            Actions = actions ?? [new ComponentAction { Type = ActionType.RemoveProvisionedAppx, Packages = ["Pkg." + id] }],
        };

    [Fact]
    public void Resolve_pulls_in_required_components_transitively()
    {
        var catalog = new CatalogDocument
        {
            Components = [Comp("a", requires: ["b"]), Comp("b", requires: ["c"]), Comp("c")],
        };

        var plan = PlanResolver.Resolve(catalog, ["a"], 26200);

        Assert.Equal(["a", "b", "c"], plan.ComponentIds);
    }

    [Fact]
    public void Resolve_throws_on_conflicting_selection()
    {
        var catalog = new CatalogDocument
        {
            Components = [Comp("a", conflicts: ["b"]), Comp("b")],
        };

        var ex = Assert.Throws<PlanResolutionException>(() => PlanResolver.Resolve(catalog, ["a", "b"], 26200));
        Assert.Single(ex.Problems);
    }

    [Fact]
    public void Resolve_throws_on_unknown_component()
    {
        var catalog = new CatalogDocument { Components = [Comp("a")] };
        Assert.Throws<PlanResolutionException>(() => PlanResolver.Resolve(catalog, ["nope"], 26200));
    }

    /// <summary>A cyclic catalog must terminate rather than recurse forever.</summary>
    [Fact]
    public void Resolve_terminates_on_a_requires_cycle()
    {
        var catalog = new CatalogDocument
        {
            Components = [Comp("a", requires: ["b"]), Comp("b", requires: ["a"])],
        };

        var plan = PlanResolver.Resolve(catalog, ["a"], 26200);
        Assert.Equal(["a", "b"], plan.ComponentIds);
    }

    /// <summary>
    /// An out-of-range build warns but does not fail — 26H1 must be usable with a caveat rather
    /// than refused. See docs/PLAN.md section 1.
    /// </summary>
    [Fact]
    public void Resolve_warns_but_succeeds_for_an_unvalidated_build()
    {
        var catalog = new CatalogDocument { Components = [Comp("a")] };

        var plan = PlanResolver.Resolve(catalog, ["a"], targetBuild: 28000);

        Assert.Single(plan.Warnings);
        Assert.Contains("28000", plan.Warnings[0].Message, StringComparison.Ordinal);
        Assert.NotEmpty(plan.ImageActions);
    }

    [Fact]
    public void Resolve_separates_registry_actions_from_image_actions()
    {
        var catalog = new CatalogDocument
        {
            Components =
            [
                Comp("a", actions:
                [
                    new ComponentAction { Type = ActionType.RemoveProvisionedAppx, Packages = ["P"] },
                    new ComponentAction
                    {
                        Type = ActionType.SetRegistry,
                        Hive = RegistryHive.Software,
                        Key = "Some\\Key",
                        ValueName = "V",
                        Kind = RegistryValueKind.Dword,
                        Data = System.Text.Json.JsonDocument.Parse("0").RootElement,
                    },
                ]),
            ],
        };

        var plan = PlanResolver.Resolve(catalog, ["a"], 26200);

        Assert.Single(plan.ImageActions);
        Assert.Single(plan.RegistryActions);
    }

    /// <summary>
    /// Appx removal must precede file deletion: a provisioned package holds references to files a
    /// prior delete would otherwise orphan.
    /// </summary>
    [Fact]
    public void Image_actions_are_ordered_appx_before_deletes()
    {
        var catalog = new CatalogDocument
        {
            Components =
            [
                Comp("a", actions:
                [
                    new ComponentAction { Type = ActionType.DeleteDirectory, Path = "Program Files\\Thing" },
                    new ComponentAction { Type = ActionType.RemoveProvisionedAppx, Packages = ["P"] },
                ]),
            ],
        };

        var plan = PlanResolver.Resolve(catalog, ["a"], 26200);

        Assert.Equal(ActionType.RemoveProvisionedAppx, plan.ImageActions[0].Action.Type);
        Assert.Equal(ActionType.DeleteDirectory, plan.ImageActions[1].Action.Type);
    }

    [Fact]
    public void Highest_risk_reflects_the_worst_selected_component()
    {
        var catalog = new CatalogDocument
        {
            Components = [Comp("safe"), Comp("bad", RiskTier.Unserviceable)],
        };

        var plan = PlanResolver.Resolve(catalog, ["safe", "bad"], 26200);

        Assert.Equal(RiskTier.Unserviceable, plan.HighestRisk);
    }
}
