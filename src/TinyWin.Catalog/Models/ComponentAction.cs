using System.Text.Json.Serialization;

namespace TinyWin.Catalog.Models;

/// <summary>
/// A single operation against a mounted offline image.
/// </summary>
/// <remarks>
/// Deliberately one flat record rather than a polymorphic hierarchy. Catalog entries are
/// hand-authored JSON, and a flat shape keeps them readable and diffable; <see cref="ActionValidator"/>
/// enforces which fields each <see cref="ActionType"/> actually requires.
/// </remarks>
public sealed record ComponentAction
{
    [JsonPropertyName("type")]
    public required ActionType Type { get; init; }

    /// <summary>Appx package family names, for <see cref="ActionType.RemoveProvisionedAppx"/>.</summary>
    [JsonPropertyName("packages")]
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>
    /// Capability / feature / package / service / scheduled-task name, depending on <see cref="Type"/>.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Path relative to the mount root. Never rooted — the engine rejects absolute paths so a
    /// catalog entry can never reach outside the image and touch the host.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Which offline hive to load, for the registry actions.</summary>
    [JsonPropertyName("hive")]
    public RegistryHive? Hive { get; init; }

    /// <summary>Key path within <see cref="Hive"/>, without the hive prefix.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Value name. Null or empty means the key's default value.</summary>
    [JsonPropertyName("valueName")]
    public string? ValueName { get; init; }

    [JsonPropertyName("kind")]
    public RegistryValueKind? Kind { get; init; }

    /// <summary>
    /// Value payload. JSON number for dword/qword, string for sz/expand-sz, array of strings
    /// for multi-sz, base64 for binary.
    /// </summary>
    [JsonPropertyName("data")]
    public System.Text.Json.JsonElement? Data { get; init; }

    /// <summary>
    /// Service start type for <see cref="ActionType.DisableService"/>. Defaults to 4 (disabled).
    /// </summary>
    [JsonPropertyName("startType")]
    public int? StartType { get; init; }

    /// <summary>
    /// Whether a missing target is expected rather than noteworthy. Defaults to false, which is
    /// what makes the no-op reporting in docs/PLAN.md section 2.1 meaningful — set this only when
    /// absence is genuinely normal (for example an edition-specific package).
    /// </summary>
    [JsonPropertyName("optional")]
    public bool Optional { get; init; }
}
