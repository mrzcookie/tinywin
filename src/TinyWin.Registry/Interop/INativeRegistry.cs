using Win32ValueKind = Microsoft.Win32.RegistryValueKind;

namespace TinyWin.Registry.Interop;

/// <summary>
/// The entire Win32 surface this project touches, behind one seam.
/// </summary>
/// <remarks>
/// Everything above this interface — session lifetime, unload retry, action semantics, no-op
/// reporting — is ordinary managed code and is unit tested against a fake. Everything below it
/// needs an elevated process and a mounted image and is verified by
/// <c>scripts/verify-offline-registry.ps1</c> instead. Keeping the line here is what makes the
/// riskiest part of the product testable at all.
///
/// Note the shape: no member returns a handle, a <c>RegistryKey</c>, or anything else with a
/// lifetime. Each call opens what it needs and closes it before returning, which is how
/// docs/PLAN.md section 3.3's "no raw RegistryKey may escape" rule is enforced structurally rather
/// than by discipline.
/// </remarks>
internal interface INativeRegistry
{
    /// <summary>
    /// Enables <c>SeBackupPrivilege</c> and <c>SeRestorePrivilege</c> on the process token.
    /// </summary>
    /// <remarks>
    /// Both are present-but-disabled by default even in an elevated process, and this omission is
    /// the single most common reason <c>RegLoadKey</c> fails with <c>ERROR_ACCESS_DENIED</c>.
    /// Implementations must be idempotent — callers enable eagerly and repeatedly.
    /// </remarks>
    void EnableHivePrivileges();

    /// <summary>Loads <paramref name="hiveFilePath"/> as <c>HKLM\<paramref name="mountName"/></c>.</summary>
    void LoadHive(string mountName, string hiveFilePath);

    /// <summary>
    /// Unloads <c>HKLM\<paramref name="mountName"/></c>. Throws on failure — callers depend on
    /// that to decide whether to force a GC and retry.
    /// </summary>
    void UnloadHive(string mountName);

    /// <summary>Immediate subkey names of <c>HKLM</c>, for stranded-hive recovery.</summary>
    IReadOnlyList<string> GetLoadedHiveNames();

    bool KeyExists(string keyPath);

    /// <summary>
    /// Reads a string value, or null if the key or value is absent or is not a string.
    /// </summary>
    /// <remarks>
    /// The only read on this interface, and it exists for exactly one reason: removing a scheduled
    /// task means looking up its GUID under <c>TaskCache\Tree</c> before the deletes can be aimed.
    /// It is deliberately not surfaced on <c>IHiveSession</c> — see <see cref="TaskCache"/>.
    /// </remarks>
    string? GetStringValue(string keyPath, string valueName);

    /// <summary>Creates <paramref name="keyPath"/> and any missing ancestors. No-op if it exists.</summary>
    void CreateKey(string keyPath);

    void SetValue(string keyPath, string valueName, object data, Win32ValueKind kind);

    /// <summary>Deletes a key and everything under it. Returns false if it was not there.</summary>
    bool DeleteKeyTree(string keyPath);

    /// <summary>Deletes one value. Returns false if the key or the value was not there.</summary>
    bool DeleteValue(string keyPath, string valueName);
}
