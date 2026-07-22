using System.Diagnostics;

namespace TinyWin.IsoBuilder;

/// <summary>Finds the two ISO writers on this machine.</summary>
/// <remarks>
/// xorriso ships with TinyWin, so its "detection" is really a check that the vendored bundle was
/// deployed — it is fetched by <c>tools/fetch-xorriso.ps1</c> rather than committed, so a fresh
/// clone that skipped that step must get a clear reason rather than a mystery failure.
/// oscdimg is genuinely detection: it comes from an ADK install we neither ship nor require.
/// </remarks>
internal static class BackendLocator
{
    private const string XorrisoExe = "xorriso.exe";
    private const string OscdimgExe = "oscdimg.exe";

    /// <summary>ADK layout, per docs/PLAN.md section 3.1 path (b).</summary>
    private const string AdkRelativeDirectory =
        @"Windows Kits\10\Assessment and Deployment Kit\Deployment Tools";

    private static readonly string[] AdkArchitectures = ["amd64", "arm64", "x86"];

    public static string? FindXorriso(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        foreach (var candidate in XorrisoCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return FindOnPath(XorrisoExe);
    }

    public static string? FindOscdimg(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        foreach (var programFiles in ProgramFilesRoots())
        {
            foreach (var architecture in AdkArchitectures)
            {
                var candidate = Path.Combine(
                    programFiles, AdkRelativeDirectory, architecture, "Oscdimg", OscdimgExe);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return FindOnPath(OscdimgExe);
    }

    /// <summary>
    /// Reads oscdimg's version from its file resources. oscdimg has no <c>--version</c> flag and
    /// prints its banner only as part of a usage error, so launching it to ask is worse than useless.
    /// </summary>
    public static string? ReadFileVersion(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static IEnumerable<string> XorrisoCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;

        yield return Path.Combine(baseDirectory, XorrisoExe);
        yield return Path.Combine(baseDirectory, "xorriso", XorrisoExe);
        yield return Path.Combine(baseDirectory, "tools", "xorriso", XorrisoExe);

        // Development layout: bin/<config>/<tfm>/ sits several levels below the repo root, where
        // tools/xorriso/ lives after tools/fetch-xorriso.ps1 has run.
        var directory = new DirectoryInfo(baseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            yield return Path.Combine(directory.FullName, "tools", "xorriso", XorrisoExe);
            directory = directory.Parent;
        }
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.ProgramFiles,
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && seen.Add(path))
            {
                yield return path;
            }
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), fileName);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
