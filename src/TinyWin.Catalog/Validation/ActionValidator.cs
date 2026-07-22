using TinyWin.Catalog.Models;

namespace TinyWin.Catalog.Validation;

/// <summary>
/// Enforces which fields each <see cref="ActionType"/> requires. The flat
/// <see cref="ComponentAction"/> shape keeps catalog JSON readable; this is what keeps it honest.
/// </summary>
public static class ActionValidator
{
    public static IEnumerable<string> Validate(ComponentAction action)
    {
        switch (action.Type)
        {
            case ActionType.RemoveProvisionedAppx:
                if (action.Packages.Count == 0)
                {
                    yield return "removeProvisionedAppx requires a non-empty 'packages' array.";
                }

                foreach (var p in action.Packages.Where(string.IsNullOrWhiteSpace))
                {
                    yield return "removeProvisionedAppx 'packages' contains a blank entry.";
                }

                break;

            case ActionType.RemoveCapability:
            case ActionType.DisableFeature:
            case ActionType.RemovePackage:
            case ActionType.RemoveScheduledTask:
            case ActionType.DisableService:
                if (string.IsNullOrWhiteSpace(action.Name))
                {
                    yield return $"{Describe(action.Type)} requires 'name'.";
                }

                break;

            case ActionType.DeleteFile:
            case ActionType.DeleteDirectory:
            case ActionType.TakeOwnership:
                foreach (var error in ValidateRelativePath(action.Path, Describe(action.Type)))
                {
                    yield return error;
                }

                break;

            case ActionType.SetRegistry:
                if (action.Hive is null)
                {
                    yield return "setRegistry requires 'hive'.";
                }

                if (string.IsNullOrWhiteSpace(action.Key))
                {
                    yield return "setRegistry requires 'key'.";
                }

                if (action.Kind is null)
                {
                    yield return "setRegistry requires 'kind'.";
                }

                if (action.Data is null)
                {
                    yield return "setRegistry requires 'data'.";
                }

                break;

            case ActionType.DeleteRegistryKey:
                if (action.Hive is null)
                {
                    yield return "deleteRegistryKey requires 'hive'.";
                }

                if (string.IsNullOrWhiteSpace(action.Key))
                {
                    yield return "deleteRegistryKey requires 'key'.";
                }

                break;

            default:
                yield return $"Unhandled action type '{action.Type}'.";
                break;
        }
    }

    /// <summary>
    /// Paths are relative to the mount root, always. An absolute or traversing path in a catalog
    /// entry would let a component reach out of the image and delete something on the host, so
    /// this is a security boundary rather than a style rule.
    /// </summary>
    private static IEnumerable<string> ValidateRelativePath(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield return $"{what} requires 'path'.";
            yield break;
        }

        if (System.IO.Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal))
        {
            yield return $"{what} 'path' must be relative to the mount root, but was '{path}'.";
        }

        var segments = path.Split('/', '\\');
        if (segments.Any(s => s == ".."))
        {
            yield return $"{what} 'path' must not traverse upwards, but was '{path}'.";
        }
    }

    private static string Describe(ActionType type) =>
        char.ToLowerInvariant(type.ToString()[0]) + type.ToString()[1..];
}
