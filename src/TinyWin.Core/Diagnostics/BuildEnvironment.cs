using System.Security.Principal;

namespace TinyWin.Core.Diagnostics;

/// <summary>
/// The machine facts the pipeline has to check: are we elevated, and is there room left.
/// </summary>
/// <remarks>
/// A seam rather than direct calls to <see cref="WindowsIdentity"/> and <see cref="DriveInfo"/>,
/// for one reason: neither can be arranged in a test. Every disk-space guard and the elevation
/// check are therefore untestable without it, and untested guards are guards that fire on the
/// wrong side of the comparison the first time they matter.
/// </remarks>
public interface IBuildEnvironment
{
    /// <summary>Whether the process can actually service an image. DISM fails with 740 otherwise.</summary>
    bool IsElevated { get; }

    /// <summary>
    /// Bytes free on the volume holding <paramref name="path"/>, or <see cref="long.MaxValue"/>
    /// when the volume cannot be measured.
    /// </summary>
    /// <remarks>
    /// Unmeasurable means "do not block": a network scratch path is unusual but legal, and refusing
    /// to build because we could not read a free-space counter would be a worse failure than the
    /// one the guard exists to prevent.
    /// </remarks>
    long GetAvailableFreeBytes(string path);
}

/// <summary>The real machine.</summary>
public sealed class SystemBuildEnvironment : IBuildEnvironment
{
    public static SystemBuildEnvironment Instance { get; } = new();

    public bool IsElevated =>
        !OperatingSystem.IsWindows() ||
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public long GetAvailableFreeBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));

            // UNC and unrooted paths have no DriveInfo. Unknown, not zero.
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return long.MaxValue;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }
}
