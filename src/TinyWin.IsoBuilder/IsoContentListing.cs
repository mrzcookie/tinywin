using System.Globalization;
using System.Text.RegularExpressions;

namespace TinyWin.IsoBuilder;

/// <summary>One file inside a finished image, as xorriso reports it.</summary>
public sealed record IsoFileEntry(string Path, long Length);

/// <summary>
/// Parses xorriso's <c>ls -l</c> style output — from both <c>-lsl</c> and
/// <c>-find / -type f -exec lsdl</c>, which share a formatter.
/// </summary>
public static partial class IsoContentListing
{
    public static IReadOnlyList<IsoFileEntry> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var entries = new List<IsoFileEntry>();

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = EntryPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var path = match.Groups["path"].Value;
            var length = long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);
            entries.Add(new IsoFileEntry(NormalizePath(path), length));
        }

        return entries;
    }

    /// <summary>
    /// Converts an image-absolute path to the relative, backslash-separated form used for the
    /// staged tree, so the two sides of a verification compare directly.
    /// </summary>
    public static string NormalizePath(string isoPath) =>
        isoPath.Trim().TrimStart('/').Replace('/', '\\');

    // -r--r--r--    1 197609   197121       4096 Mar  6 19:03 '/boot/etfsboot.com'
    // Only regular files are of interest; directory lines start with 'd'.
    [GeneratedRegex(
        @"^-\S{9}\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+\S+\s+\S+\s+\S+\s+'(?<path>.*)'\s*$",
        RegexOptions.ExplicitCapture)]
    private static partial Regex EntryPattern();
}
