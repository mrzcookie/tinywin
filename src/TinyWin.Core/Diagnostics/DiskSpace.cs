namespace TinyWin.Core.Diagnostics;

/// <summary>
/// A stage refused to start because the volume it writes to does not have room for it.
/// </summary>
/// <remarks>
/// Distinct from a plain <see cref="IOException"/> on purpose. Running out of disk halfway through
/// <c>/Export-Image</c> surfaces as "The system cannot find the path specified" or a truncated WIM
/// from deep inside DISM, forty minutes in, with no indication of the actual cause. Checking before
/// each consuming stage turns that into a sentence naming the volume, the shortfall and the fix.
/// </remarks>
public sealed class InsufficientDiskSpaceException : IOException
{
    public InsufficientDiskSpaceException()
    {
    }

    public InsufficientDiskSpaceException(string message)
        : base(message)
    {
    }

    public InsufficientDiskSpaceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InsufficientDiskSpaceException(string operation, string path, long requiredBytes, long availableBytes)
        : base(BuildMessage(operation, path, requiredBytes, availableBytes))
    {
        Operation = operation;
        Path = path;
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    /// <summary>What was about to happen, phrased for a user: "Recompressing the image".</summary>
    public string Operation { get; } = string.Empty;

    /// <summary>The path whose volume was checked.</summary>
    public string Path { get; } = string.Empty;

    public long RequiredBytes { get; }

    public long AvailableBytes { get; }

    private static string BuildMessage(string operation, string path, long requiredBytes, long availableBytes)
    {
        var volume = VolumeOf(path);
        var shortfall = Math.Max(0, requiredBytes - availableBytes);

        return $"{operation} needs about {ByteSize.Format(requiredBytes)} free on {volume}, " +
            $"but only {ByteSize.Format(availableBytes)} is available. " +
            $"Free up {ByteSize.Format(shortfall)} on {volume}, or restart the build with a scratch " +
            "directory on a volume with more room.";
    }

    private static string VolumeOf(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? path : root;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}

/// <summary>
/// Progressive free-space guards, one per stage that consumes real space.
/// </summary>
/// <remarks>
/// <c>PreflightStage</c> checks 25 GB once, up front, which is necessary but not
/// sufficient: the copy, the ESD export, the recompress and the ISO write each consume gigabytes
/// at different points, and the ISO write usually lands on a *different* volume from the scratch
/// directory. Each stage therefore re-checks what it is about to need.
///
/// The multipliers below are deliberately conservative and are estimates, not measurements — no
/// elevated run has been possible yet (docs/findings/hardening.md section 4). They are sized to
/// fail early rather than to be exact.
/// </remarks>
public static class DiskSpace
{
    /// <summary>Copying the ISO tree writes every byte of the source, plus filesystem overhead.</summary>
    public const double StagingOverhead = 1.05;

    /// <summary>
    /// ESD is solid-compressed; exporting it to a maximum-compression WIM roughly doubles it, and
    /// both files exist at once until the ESD is deleted.
    /// </summary>
    public const double EsdToWimOverhead = 3.0;

    /// <summary>Export writes install2.wim alongside install.wim before replacing it.</summary>
    public const double RecompressOverhead = 1.05;

    /// <summary>The ISO is the staged tree plus descriptor overhead.</summary>
    public const double IsoOverhead = 1.02;

    /// <summary>
    /// Mounting materialises the image's directory tree and copies every file that changes.
    /// Proportional to the image, with a floor for small ones.
    /// </summary>
    public const double MountFraction = 0.10;

    public const long MountFloorBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// <c>StartComponentCleanup /ResetBase</c> rewrites the component store inside the mount and
    /// needs room for both copies of what it touches.
    /// </summary>
    public const double CleanupFraction = 0.25;

    public const long CleanupFloorBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Throws when <paramref name="path"/>'s volume cannot hold <paramref name="requiredBytes"/>.</summary>
    public static void Require(
        IBuildEnvironment environment, string path, long requiredBytes, string operation)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (requiredBytes <= 0)
        {
            return;
        }

        var available = environment.GetAvailableFreeBytes(path);
        if (available < requiredBytes)
        {
            throw new InsufficientDiskSpaceException(operation, path, requiredBytes, available);
        }
    }

    /// <summary>Total bytes of every file under <paramref name="directory"/>, for the ISO guard.</summary>
    public static long MeasureDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished mid-walk contributes nothing; the guard is an estimate.
            }
        }

        return total;
    }

    /// <summary>File length, or 0 when it cannot be read. Guards estimate; they never throw.</summary>
    public static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Applies a multiplier without overflowing on a long.</summary>
    public static long Scale(long bytes, double factor) =>
        bytes <= 0 ? 0 : (long)Math.Min(long.MaxValue, bytes * factor);
}

/// <summary>Byte counts rendered the way the messages and the build report want them.</summary>
public static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes == long.MaxValue)
        {
            return "an unknown amount";
        }

        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Abs(bytes);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var sign = bytes < 0 ? "-" : string.Empty;
        return unit switch
        {
            0 => $"{sign}{value:0} bytes",
            <= 2 => $"{sign}{value:0.#} {units[unit]}",
            _ => $"{sign}{value:0.##} {units[unit]}",
        };
    }
}
