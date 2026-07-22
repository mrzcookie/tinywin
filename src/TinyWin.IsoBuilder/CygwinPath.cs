using System.Text;

namespace TinyWin.IsoBuilder;

/// <summary>
/// Converts Windows paths into the POSIX form the vendored MSYS2 xorriso build requires.
/// </summary>
/// <remarks>
/// This is not cosmetic. xorriso prepends its own working directory to any argument that does
/// not start with <c>/</c>, so <c>C:\scratch\tree</c> and <c>C:/scratch/tree</c> are both treated
/// as <em>relative</em> and produce a confusing "Cannot determine attributes of source file"
/// failure naming a nonsense path. Every path handed to xorriso goes through here first.
/// See docs/spikes/iso-build.md section 3, cost (a).
/// </remarks>
public static class CygwinPath
{
    private const string Prefix = "/cygdrive/";

    /// <summary>
    /// Converts an absolute Windows path to its <c>/cygdrive/&lt;drive&gt;/...</c> equivalent.
    /// A path that is already in POSIX form is returned unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The path is empty, relative, or drive-relative. Passing any of those to xorriso silently
    /// resolves against xorriso's working directory, which is never what the caller meant.
    /// </exception>
    public static string FromWindows(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var trimmed = path.Trim();

        // Already POSIX — the caller converted it, or it came back out of a xorriso report.
        if (trimmed[0] is '/')
        {
            return Normalize(trimmed);
        }

        var stripped = StripExtendedLengthPrefix(trimmed, out var wasUncExtended);

        if (wasUncExtended || IsUnc(stripped))
        {
            // MSYS2 maps //server/share onto the UNC namespace.
            var unc = stripped.TrimStart('\\', '/');
            if (unc.Length == 0)
            {
                throw new ArgumentException($"'{path}' is not a usable UNC path.", nameof(path));
            }

            return Normalize("//" + unc);
        }

        if (stripped.Length < 2 || stripped[1] != ':' || !char.IsAsciiLetter(stripped[0]))
        {
            throw new ArgumentException(
                $"'{path}' is not an absolute Windows path. xorriso resolves relative paths " +
                "against its own working directory, so only absolute paths may be passed to it.",
                nameof(path));
        }

        if (stripped.Length == 2)
        {
            // "C:" — the current directory *on* drive C, not the root of drive C.
            throw new ArgumentException(
                $"'{path}' is drive-relative. Use a rooted path such as '{stripped}\\'.",
                nameof(path));
        }

        if (stripped[2] is not ('\\' or '/'))
        {
            throw new ArgumentException(
                $"'{path}' is drive-relative. Use a rooted path such as '{stripped[..2]}\\...'.",
                nameof(path));
        }

        var drive = char.ToLowerInvariant(stripped[0]);
        var rest = stripped[2..];

        return Normalize(Prefix + drive + rest);
    }

    /// <summary>
    /// Converts a path relative to the ISO root (<c>efi\microsoft\boot\efisys.bin</c>) into the
    /// forward-slash form xorriso uses for <c>-b</c> and <c>-e</c>. These are image-internal
    /// paths, so they stay relative and must not get a <c>/cygdrive</c> prefix.
    /// </summary>
    public static string ToIsoRelative(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("An ISO-relative path cannot be the root.", nameof(relativePath));
        }

        return CollapseSlashes(normalized);
    }

    private static string StripExtendedLengthPrefix(string path, out bool wasUncExtended)
    {
        wasUncExtended = false;

        // \\?\UNC\server\share and \\?\C:\dir — the Win32 long-path escape hatch.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var rest = path[4..];
            if (rest.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase))
            {
                wasUncExtended = true;
                return @"\\" + rest[4..];
            }

            return rest;
        }

        return path;
    }

    private static bool IsUnc(string path) =>
        path.Length > 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';

    private static string Normalize(string posixPath)
    {
        var collapsed = CollapseSlashes(posixPath.Replace('\\', '/'));

        // "/cygdrive/c/" is the root of C:. Anywhere else a trailing slash is noise, and xorriso
        // reports the two forms differently in its own output.
        if (collapsed.Length > 1 && collapsed.EndsWith('/'))
        {
            collapsed = collapsed.TrimEnd('/');
            if (collapsed.Length == 0)
            {
                collapsed = "/";
            }
        }

        return collapsed;
    }

    private static string CollapseSlashes(string value)
    {
        if (!value.Contains("//", StringComparison.Ordinal))
        {
            return value;
        }

        // Preserve a leading "//" — that is a UNC root, not a redundant separator.
        var leading = value.StartsWith("//", StringComparison.Ordinal) ? "//" : string.Empty;
        var body = value[leading.Length..];

        var builder = new StringBuilder(value.Length);
        builder.Append(leading);

        var previousWasSlash = false;
        foreach (var c in body)
        {
            if (c == '/')
            {
                if (previousWasSlash)
                {
                    continue;
                }

                previousWasSlash = true;
            }
            else
            {
                previousWasSlash = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
