namespace TinyWin.IsoBuilder;

/// <summary>A staged-tree entry whose name Joliet cannot carry intact.</summary>
public sealed record IsoNameViolation(string RelativePath, string Name, int NameLength, bool IsDirectory);

/// <summary>What the preflight learned about the staged tree.</summary>
public sealed record IsoPreflightResult
{
    /// <summary>Names Joliet would silently truncate. Non-empty means the build must not run.</summary>
    public required IReadOnlyList<IsoNameViolation> NameViolations { get; init; }

    /// <summary>Non-fatal deviations the build report should surface.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public required int FileCount { get; init; }

    public required int DirectoryCount { get; init; }

    public required long TotalBytes { get; init; }

    public required long LargestFileBytes { get; init; }

    public string? LargestFilePath { get; init; }

    /// <summary>
    /// True when some member file exceeds the ISO 9660 single-extent ceiling, so the image
    /// depends on level 3 multi-extent. That is the one property of our output WinPE has never
    /// been proven to read — see docs/spikes/iso-build.md section 2b.
    /// </summary>
    public bool RequiresMultiExtent => LargestFileBytes > uint.MaxValue;

    public bool CanBuild => NameViolations.Count == 0;
}

/// <summary>
/// Checks a staged ISO tree before anything is written.
/// </summary>
/// <remarks>
/// <para>
/// Joliet caps basenames at 103 characters with <c>-joliet-long</c> (64 without). xorriso does
/// not refuse an over-long name — it truncates it, and the resulting media looks fine until
/// Windows Setup cannot find a file. Emitting that silently would violate the "report no-ops,
/// never silently swallow" rule in CLAUDE.md, so this fails the build instead.
/// </para>
/// <para>
/// The volume label is a softer case: Joliet's volume descriptor holds 16 characters, so
/// Microsoft's own 23-character <c>CCCOMA_X64FRE_EN-US_DV9</c> comes back from Windows as
/// <c>CCCOMA_X64FRE_EN</c>. Windows Setup does not care, so that is reported as a warning
/// rather than a refusal — otherwise stock 25H2 media could not be rebuilt at all.
/// </para>
/// </remarks>
public static class IsoPreflight
{
    /// <summary>Longest basename Joliet can carry when <c>-joliet-long</c> is in effect.</summary>
    public const int MaxJolietBasenameLength = 103;

    /// <summary>Longest volume id the Joliet volume descriptor can carry.</summary>
    public const int MaxJolietVolumeLabelLength = 16;

    public static IsoPreflightResult Inspect(string sourceDirectory, string? volumeLabel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        var root = new DirectoryInfo(sourceDirectory);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"Staged ISO tree '{sourceDirectory}' does not exist.");
        }

        var violations = new List<IsoNameViolation>();
        var warnings = new List<string>();
        var files = 0;
        var directories = 0;
        long totalBytes = 0;
        long largestBytes = 0;
        string? largestPath = null;

        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root.FullName, entry.FullName);
            var isDirectory = entry is DirectoryInfo;

            if (isDirectory)
            {
                directories++;
            }
            else
            {
                files++;
                var length = ((FileInfo)entry).Length;
                totalBytes += length;
                if (length > largestBytes)
                {
                    largestBytes = length;
                    largestPath = relative;
                }
            }

            if (entry.Name.Length > MaxJolietBasenameLength)
            {
                violations.Add(new IsoNameViolation(relative, entry.Name, entry.Name.Length, isDirectory));
            }
        }

        if (files == 0)
        {
            warnings.Add($"The staged tree '{root.FullName}' contains no files.");
        }

        if (!string.IsNullOrEmpty(volumeLabel) && volumeLabel.Length > MaxJolietVolumeLabelLength)
        {
            warnings.Add(
                $"Volume label '{volumeLabel}' is {volumeLabel.Length} characters; Joliet carries " +
                $"{MaxJolietVolumeLabelLength}, so Windows will report it as " +
                $"'{volumeLabel[..MaxJolietVolumeLabelLength]}'.");
        }

        return new IsoPreflightResult
        {
            NameViolations = violations,
            Warnings = warnings,
            FileCount = files,
            DirectoryCount = directories,
            TotalBytes = totalBytes,
            LargestFileBytes = largestBytes,
            LargestFilePath = largestPath,
        };
    }

    /// <summary>Throws with every offending name listed, rather than emitting truncated media.</summary>
    public static void ThrowIfUnbuildable(IsoPreflightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.CanBuild)
        {
            return;
        }

        var detail = string.Join(
            Environment.NewLine,
            result.NameViolations.Select(v => $"  {v.NameLength} chars: {v.RelativePath}"));

        throw new IsoBuilderException(
            $"{result.NameViolations.Count} name(s) in the staged tree exceed the " +
            $"{MaxJolietBasenameLength}-character Joliet limit. xorriso would truncate them without " +
            "complaint and the resulting media would be missing files:" + Environment.NewLine + detail);
    }
}
