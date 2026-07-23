using TinyWin.Core.Diagnostics;

namespace TinyWin.Tests.Fakes;

/// <summary>
/// A machine with whatever elevation and free space the test needs.
/// </summary>
/// <remarks>
/// The disk-space guards cannot be tested any other way: filling a real volume to 2 GB free is not
/// something a unit test gets to do, and a guard nobody exercises is a comparison nobody has ever
/// checked the direction of.
/// </remarks>
public sealed class FakeBuildEnvironment : IBuildEnvironment
{
    /// <summary>Free bytes reported for any path with no specific entry. Defaults to 500 GB.</summary>
    public long DefaultFreeBytes { get; set; } = 500L * 1024 * 1024 * 1024;

    /// <summary>Per-volume overrides, keyed by path root or by any prefix of the queried path.</summary>
    public Dictionary<string, long> FreeBytesByPrefix { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsElevated { get; set; } = true;

    public List<string> Queried { get; } = [];

    public long GetAvailableFreeBytes(string path)
    {
        Queried.Add(path);

        foreach (var (prefix, free) in FreeBytesByPrefix)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return free;
            }
        }

        return DefaultFreeBytes;
    }
}
