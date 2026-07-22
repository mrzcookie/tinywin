using System.Text.Json.Serialization;
using TinyWin.Catalog.Models;

namespace TinyWin.Catalog;

/// <summary>The loaded catalog: every component and preset TinyWin knows about.</summary>
public sealed record CatalogDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("components")]
    public IReadOnlyList<Component> Components { get; init; } = [];

    [JsonPropertyName("presets")]
    public IReadOnlyList<Preset> Presets { get; init; } = [];

    public Component? Find(string id) =>
        Components.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public static CatalogDocument Merge(IEnumerable<CatalogDocument> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var list = parts.ToList();
        return new CatalogDocument
        {
            SchemaVersion = list.Count > 0 ? list[0].SchemaVersion : 1,
            Components = [.. list.SelectMany(p => p.Components)],
            Presets = [.. list.SelectMany(p => p.Presets)],
        };
    }
}
