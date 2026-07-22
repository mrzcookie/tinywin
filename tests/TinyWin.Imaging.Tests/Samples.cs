namespace TinyWin.Imaging.Tests;

/// <summary>Loads the captured DISM output under <c>Samples/</c>. See that directory's README for provenance.</summary>
internal static class Samples
{
    public static string Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", name);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"Missing DISM output sample '{name}'.", path);
    }

    public static string WimInfoList => Load("get-wiminfo-list.txt");

    public static string WimInfoIndex1 => Load("get-wiminfo-index1.txt");

    public static string WimInfoIndex6 => Load("get-wiminfo-index6.txt");

    public static string MountedWimInfo => Load("get-mountedwiminfo.txt");

    public static string ProvisionedAppx => Load("get-provisionedappxpackages.txt");

    public static string Capabilities => Load("get-capabilities.txt");

    public static string Features => Load("get-features.txt");

    public static string Packages => Load("get-packages.txt");

    public static string Error740 => Load("error-740.txt");

    /// <summary>
    /// Real <c>/Mount-Wim</c> output including all 98 progress-bar repaints.
    /// </summary>
    /// <remarks>
    /// Stored one repaint per line because that is how the capture was written to disk. The raw
    /// stream interleaved carriage returns — see <see cref="MountProgressAsRawStream"/>.
    /// </remarks>
    public static string MountProgress => Load("mount-progress.txt");

    /// <summary>
    /// Reconstructs the byte-level stream DISM actually produced, so the reader is tested against
    /// the real encoding rather than a tidied-up version of it.
    /// </summary>
    /// <remarks>
    /// The reconstruction is pinned by the byte counts in the capture, not guessed: 0 backspaces,
    /// 202 CR, 104 LF over 98 bar repaints and 6 ordinary lines. 98x2 + 6 = 202 and 98 + 6 = 104,
    /// which is only consistent with each repaint being <c>\r</c> + bar + <c>\r\n</c> and each
    /// ordinary line being CRLF-terminated.
    /// </remarks>
    public static string MountProgressAsRawStream()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in MountProgress.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith('['))
            {
                builder.Append('\r').Append(text).Append("\r\n");
            }
            else
            {
                builder.Append(text).Append("\r\n");
            }
        }

        return builder.ToString();
    }
}
