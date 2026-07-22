namespace TinyWin.IsoBuilder;

/// <summary>Result of copying a tree, so callers can report counts rather than "done".</summary>
public sealed record TreeCopyResult(int FileCount, int DirectoryCount, long TotalBytes);

/// <summary>
/// Copies an extracted ISO tree to the scratch directory.
/// </summary>
/// <remarks>
/// Every file on optical media is read-only, and a plain copy carries that attribute across. The
/// later pipeline stages write into this tree — DISM mounts into it, the registry stage rewrites
/// hives, the unattend stage drops a file at the root — so the attribute is cleared on the way in.
/// Leaving it would produce access-denied failures several stages later, far from the cause.
/// </remarks>
internal static class TreeCopier
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<TreeCopyResult> CopyAsync(
        string sourceRoot,
        string destinationRoot,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var source = new DirectoryInfo(sourceRoot);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException($"Source '{sourceRoot}' does not exist.");
        }

        Directory.CreateDirectory(destinationRoot);

        var files = source.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var totalBytes = files.Sum(f => f.Length);
        var directories = 0;

        foreach (var directory in source.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(
                Path.Combine(destinationRoot, Path.GetRelativePath(source.FullName, directory.FullName)));
            directories++;
        }

        long copiedBytes = 0;
        var lastReported = -1.0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source.FullName, file.FullName);
            var target = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await CopyFileAsync(file.FullName, target, cancellationToken).ConfigureAwait(false);
            File.SetAttributes(target, FileAttributes.Normal);

            copiedBytes += file.Length;

            if (progress is not null && totalBytes > 0)
            {
                var fraction = (double)copiedBytes / totalBytes;
                if (fraction - lastReported >= 0.005 || copiedBytes == totalBytes)
                {
                    lastReported = fraction;
                    progress.Report(fraction);
                }
            }
        }

        return new TreeCopyResult(files.Count, directories, totalBytes);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        // Clear the attribute before opening: a previous cancelled run can leave a read-only file
        // here, and File.Create would then fail.
        if (File.Exists(destination))
        {
            File.SetAttributes(destination, FileAttributes.Normal);
        }

        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
    }
}
