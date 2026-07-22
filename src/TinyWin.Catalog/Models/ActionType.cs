using System.Text.Json.Serialization;

namespace TinyWin.Catalog.Models;

/// <summary>
/// The closed set of things a catalog component is allowed to do to an offline image.
/// Closed on purpose: every member has exactly one executor and one set of unit tests,
/// so catalog authors extend the data, never the engine. See docs/PLAN.md section 2.1.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActionType>))]
public enum ActionType
{
    RemoveProvisionedAppx,
    RemoveCapability,
    DisableFeature,
    RemovePackage,
    SetRegistry,
    DeleteRegistryKey,
    DeleteFile,
    DeleteDirectory,
    RemoveScheduledTask,
    DisableService,
    TakeOwnership,
}
