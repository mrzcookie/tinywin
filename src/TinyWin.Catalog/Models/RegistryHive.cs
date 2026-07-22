using System.Text.Json.Serialization;

namespace TinyWin.Catalog.Models;

/// <summary>
/// The five offline hives TinyWin loads from a mounted image, matching tiny11's z-prefixed
/// mount points. See docs/PLAN.md section 3.3 for the unload hazard these carry.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegistryHive>))]
public enum RegistryHive
{
    /// <summary>Windows\System32\config\COMPONENTS</summary>
    Components,

    /// <summary>Windows\System32\config\default</summary>
    Default,

    /// <summary>Users\Default\ntuser.dat — the template every new user profile is built from.</summary>
    NtUser,

    /// <summary>Windows\System32\config\SOFTWARE</summary>
    Software,

    /// <summary>Windows\System32\config\SYSTEM</summary>
    System,
}

/// <summary>
/// Registry value types TinyWin can write. A subset of the Win32 set, by design, and named after
/// the REG_* constants rather than the .NET ones so catalog JSON reads like regedit.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegistryValueKind>))]
public enum RegistryValueKind
{
    Dword,
    Qword,
    Sz,
    ExpandSz,
    MultiSz,
    Binary,
}
