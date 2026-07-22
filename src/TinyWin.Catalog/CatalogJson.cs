using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyWin.Catalog;

/// <summary>Shared serializer settings. Catalog JSON is hand-authored, so it must tolerate comments.</summary>
public static class CatalogJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // An unrecognised property is nearly always a typo in a hand-written entry, and a silently
        // ignored typo is exactly the failure mode the catalog design is trying to avoid.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
