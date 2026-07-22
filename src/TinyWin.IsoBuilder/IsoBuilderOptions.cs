using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder;

/// <summary>Host-supplied configuration for <see cref="IsoBuilderService"/>.</summary>
public sealed record IsoBuilderOptions
{
    /// <summary>
    /// Explicit path to <c>xorriso.exe</c>. Null means "probe": next to the app, then
    /// <c>tools/xorriso</c> walking up from the app directory, then <c>PATH</c>.
    /// </summary>
    public string? XorrisoPath { get; init; }

    /// <summary>
    /// Explicit path to <c>oscdimg.exe</c>. Null means "probe the installed ADK".
    /// </summary>
    public string? OscdimgPath { get; init; }

    /// <summary>
    /// Which backend to use when both are available. xorriso is primary because it ships with the
    /// app; oscdimg is the runtime-selectable fallback (docs/PLAN.md section 3.1 path (b)).
    /// </summary>
    public IsoBackendKind PreferredBackend { get; init; } = IsoBackendKind.Xorriso;

    /// <summary>
    /// Compare the finished image against the staged tree file-for-file before returning. On by
    /// default: a dropped or truncated file otherwise surfaces only when the user tries to boot.
    /// </summary>
    public bool VerifyAfterBuild { get; init; } = true;

    /// <summary>Path to <c>dism.exe</c>, used only for the install.wim split escape hatch.</summary>
    public string DismPath { get; init; } = "dism.exe";
}
