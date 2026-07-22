namespace TinyWin.Registry;

/// <summary>
/// Normalises and validates the <c>key</c> field of a catalog registry action.
/// </summary>
/// <remarks>
/// Catalog entries are hand-authored, and the two mistakes that actually happen are pasting a full
/// <c>HKLM\SOFTWARE\...</c> path out of regedit and using forward slashes. Both are rejected loudly
/// rather than silently repaired: a path we quietly rewrite is a path whose action might land
/// somewhere the author did not intend, and the no-op reporting in docs/PLAN.md section 2.1 would
/// then be reporting on the wrong key.
/// </remarks>
internal static class RegistryKeyPath
{
    private static readonly string[] HivePrefixes =
    [
        "HKEY_LOCAL_MACHINE", "HKLM",
        "HKEY_CURRENT_USER", "HKCU",
        "HKEY_USERS", "HKU",
        "HKEY_CLASSES_ROOT", "HKCR",
        "HKEY_CURRENT_CONFIG", "HKCC",
    ];

    /// <summary>
    /// Returns the key path in canonical form: backslash separated, no leading or trailing
    /// separator, no empty segments.
    /// </summary>
    /// <exception cref="RegistryActionException">The path is absent or malformed.</exception>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new RegistryActionException("Registry action requires a non-empty 'key'.");
        }

        var segments = key.Trim()
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            throw new RegistryActionException($"Registry action 'key' resolved to nothing: '{key}'.");
        }

        if (HivePrefixes.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            throw new RegistryActionException(
                $"Registry action 'key' must be relative to the hive, but was '{key}'. "
                + $"Drop the '{segments[0]}' prefix and set 'hive' instead.");
        }

        if (HiveLayout.IsTinyWinMountName(segments[0]))
        {
            throw new RegistryActionException(
                $"Registry action 'key' must not include TinyWin's mount point, but was '{key}'. "
                + "Mount points are an implementation detail; set 'hive' instead.");
        }

        if (segments.Contains(".."))
        {
            throw new RegistryActionException($"Registry action 'key' must not traverse upwards, but was '{key}'.");
        }

        // A colon would mean someone pasted a file path, or is trying to reach a named stream.
        if (key.Contains(':', StringComparison.Ordinal))
        {
            throw new RegistryActionException($"Registry action 'key' must not contain ':', but was '{key}'.");
        }

        return string.Join('\\', segments);
    }

    /// <summary>
    /// Joins a mount point and a normalised key path into the <c>HKLM</c>-relative path the Win32
    /// calls actually take.
    /// </summary>
    public static string UnderMount(string mountName, string normalizedKey) => $"{mountName}\\{normalizedKey}";

    /// <summary>Splits a normalised path into parent and leaf; parent is null for a top-level key.</summary>
    public static (string? Parent, string Leaf) SplitLeaf(string normalizedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedKey);

        var index = normalizedKey.LastIndexOf('\\');
        return index < 0
            ? (null, normalizedKey)
            : (normalizedKey[..index], normalizedKey[(index + 1)..]);
    }
}
