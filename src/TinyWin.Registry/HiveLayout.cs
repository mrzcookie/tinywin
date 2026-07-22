using TinyWin.Catalog.Models;

namespace TinyWin.Registry;

/// <summary>
/// Where each offline hive lives inside a mounted image, and what we call it once loaded.
/// </summary>
/// <remarks>
/// Pure string work, kept separate from the P/Invoke layer so it can be unit tested without an
/// elevated process or a mounted WIM. The mount names carry a TinyWin-specific prefix rather than
/// tiny11's bare <c>zSOFTWARE</c> so that crash recovery (see
/// <see cref="OfflineRegistry.UnloadStrandedHivesAsync"/>) can never unload a hive some other tool
/// on the machine owns.
/// </remarks>
internal static class HiveLayout
{
    /// <summary>
    /// Prefix on every mount point we create under <c>HKLM</c>. Still <c>z</c>-initial, per
    /// docs/PLAN.md section 3.3, so it sorts to the bottom of regedit and is obvious to a human
    /// staring at a stranded machine.
    /// </summary>
    public const string MountPrefix = "zTW-";

    public static IReadOnlyList<RegistryHive> All { get; } =
        [RegistryHive.Components, RegistryHive.Default, RegistryHive.NtUser, RegistryHive.Software, RegistryHive.System];

    /// <summary>The <c>HKLM</c> subkey name this hive is loaded as.</summary>
    public static string MountName(RegistryHive hive) => MountPrefix + hive switch
    {
        RegistryHive.Components => "COMPONENTS",
        RegistryHive.Default => "DEFAULT",
        RegistryHive.NtUser => "NTUSER",
        RegistryHive.Software => "SOFTWARE",
        RegistryHive.System => "SYSTEM",
        _ => throw new RegistryActionException($"Unknown registry hive '{hive}'."),
    };

    /// <summary>The hive file's path relative to the image mount root.</summary>
    public static string RelativeFilePath(RegistryHive hive) => hive switch
    {
        RegistryHive.Components => @"Windows\System32\config\COMPONENTS",
        RegistryHive.Default => @"Windows\System32\config\default",

        // The template every new user profile is cloned from — this is why a per-user tweak
        // applied here reaches accounts that do not exist yet.
        RegistryHive.NtUser => @"Users\Default\ntuser.dat",
        RegistryHive.Software => @"Windows\System32\config\SOFTWARE",
        RegistryHive.System => @"Windows\System32\config\SYSTEM",
        _ => throw new RegistryActionException($"Unknown registry hive '{hive}'."),
    };

    /// <summary>The absolute hive file path for an image mounted at <paramref name="mountPath"/>.</summary>
    public static string FilePath(string mountPath, RegistryHive hive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPath);
        return Path.Combine(mountPath, RelativeFilePath(hive));
    }

    /// <summary>
    /// Whether an <c>HKLM</c> subkey name is one of ours. Used by crash recovery, which must not
    /// touch mount points belonging to another tool.
    /// </summary>
    public static bool IsTinyWinMountName(string? name) =>
        !string.IsNullOrEmpty(name) && name.StartsWith(MountPrefix, StringComparison.OrdinalIgnoreCase);
}
