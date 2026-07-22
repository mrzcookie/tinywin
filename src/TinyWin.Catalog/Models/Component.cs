using System.Text.Json.Serialization;

namespace TinyWin.Catalog.Models;

/// <summary>
/// One user-selectable thing to remove. The unit of modularity — see docs/PLAN.md section 2.1.
/// </summary>
public sealed record Component
{
    /// <summary>Stable dotted id, for example <c>apps.edge.webview2</c>. Never reused or renamed.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("risk")]
    public required RiskTier Risk { get; init; }

    /// <summary>Presets that select this component by default.</summary>
    [JsonPropertyName("defaultIn")]
    public IReadOnlyList<string> DefaultIn { get; init; } = [];

    /// <summary>Rough disk saving, used only for the live estimate in the UI header.</summary>
    [JsonPropertyName("estimatedSavingsMb")]
    public int EstimatedSavingsMb { get; init; }

    [JsonPropertyName("appliesTo")]
    public required BuildRange AppliesTo { get; init; }

    /// <summary>Component ids that must also be selected for this one to make sense.</summary>
    [JsonPropertyName("requires")]
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Component ids that cannot be selected alongside this one.</summary>
    [JsonPropertyName("conflicts")]
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>
    /// Plain-language consequences, shown in the UI flyout and the Review page. Required and
    /// non-empty for anything above <see cref="RiskTier.Safe"/> — telling users what breaks is
    /// the product's main safety mechanism, so the validator enforces it.
    /// </summary>
    [JsonPropertyName("breaks")]
    public IReadOnlyList<string> Breaks { get; init; } = [];

    [JsonPropertyName("actions")]
    public required IReadOnlyList<ComponentAction> Actions { get; init; }
}

/// <summary>
/// Inclusive range of Windows build numbers a component has been validated against.
/// For v1 this is almost always 26100-26299, covering 24H2 and 25H2 as one servicing branch.
/// </summary>
public sealed record BuildRange
{
    [JsonPropertyName("minBuild")]
    public int? MinBuild { get; init; }

    [JsonPropertyName("maxBuild")]
    public int? MaxBuild { get; init; }

    public bool Includes(int build) =>
        (MinBuild is null || build >= MinBuild) &&
        (MaxBuild is null || build <= MaxBuild);
}
