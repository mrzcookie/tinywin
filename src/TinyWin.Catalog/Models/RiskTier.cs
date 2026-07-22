using System.Text.Json.Serialization;

namespace TinyWin.Catalog.Models;

/// <summary>
/// How much damage a component can do. Drives UI colour, preset membership, and which
/// confirmation gate the Review page applies. See docs/PLAN.md section 2.1.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskTier>))]
public enum RiskTier
{
    /// <summary>Removable with no functional loss a typical user would notice.</summary>
    Safe,

    /// <summary>Loses a feature some people rely on, but the OS is unaffected.</summary>
    Caution,

    /// <summary>Known to break unrelated software or a core workflow. Opt-in only.</summary>
    Breaking,

    /// <summary>
    /// Leaves an image Microsoft will not service, and which cannot be repaired in place.
    /// Requires a typed confirmation on the Review page, not merely a click.
    /// </summary>
    Unserviceable,
}
