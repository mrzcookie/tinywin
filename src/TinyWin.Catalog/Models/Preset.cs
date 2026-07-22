using System.Text.Json.Serialization;

namespace TinyWin.Catalog.Models;

/// <summary>
/// A named selection of components. Presets carry no behaviour of their own — they are just
/// id lists, which is what keeps "preset" and "custom" the same code path.
/// </summary>
public sealed record Preset
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Ordering hint for the UI dropdown, ascending aggression.</summary>
    [JsonPropertyName("order")]
    public int Order { get; init; }

    /// <summary>
    /// The highest risk tier this preset is allowed to contain. The validator fails the build if
    /// a component's <c>defaultIn</c> puts it in a preset that is not cleared for its tier —
    /// that is what stops Defender from silently landing in "balanced".
    /// </summary>
    [JsonPropertyName("maxRisk")]
    public required RiskTier MaxRisk { get; init; }

    /// <summary>
    /// Explicit additions beyond the components that name this preset in <c>defaultIn</c>.
    /// Usually empty; <c>defaultIn</c> is the normal mechanism.
    /// </summary>
    [JsonPropertyName("includes")]
    public IReadOnlyList<string> Includes { get; init; } = [];

    /// <summary>Component ids to exclude even if they name this preset in <c>defaultIn</c>.</summary>
    [JsonPropertyName("excludes")]
    public IReadOnlyList<string> Excludes { get; init; } = [];
}
