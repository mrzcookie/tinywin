namespace TinyWin.Core.Diagnostics;

/// <summary>
/// Turns an exception from any stage into a sentence saying what to do about it.
/// </summary>
/// <remarks>
/// <para>
/// The failures this pipeline produces are nearly all recoverable by the user — run elevated,
/// unload a stuck hive, fetch xorriso, free some space — and nearly all of them arrive as a
/// message describing only what broke. This maps the ones we can recognise onto the action that
/// fixes them, and says nothing when it does not recognise one. A guessed remedy is worse than
/// none.
/// </para>
/// <para>
/// Matching is by exception type <em>name</em> and message content rather than by type, because
/// <c>TinyWin.Core</c> deliberately cannot reference <c>TinyWin.Imaging</c>,
/// <c>TinyWin.Registry</c> or <c>TinyWin.IsoBuilder</c> — the dependency direction in
/// docs/PLAN.md section 2 points the other way. String matching is the price of that boundary;
/// the alternative is a Core that knows about every engine, which is the coupling the boundary
/// exists to prevent. The type names are asserted in tests so a rename cannot silently drop the
/// advice.
/// </para>
/// </remarks>
public static class FailureAdvice
{
    /// <summary>The remedy for <paramref name="exception"/>, or null when there is nothing useful to add.</summary>
    public static string? For(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        // Cancellation is not a failure and its advice is about what survived, not what to fix.
        if (exception is OperationCanceledException)
        {
            return "Cancelled. Any mounted image was dismounted and discarded. The staged files are " +
                "kept, so re-running the same build with --resume continues from the last completed stage.";
        }

        if (exception is InsufficientDiskSpaceException)
        {
            // The message already names the volume, the shortfall and the fix.
            return null;
        }

        var name = exception.GetType().Name;
        var message = exception.Message;

        if (name == "DismElevationRequiredException" || MentionsElevation(message))
        {
            return "Run TinyWin as Administrator — right-click it and choose 'Run as administrator'. " +
                "DISM performs its elevation check before any work, so even read-only operations fail " +
                "with error 740 without it.";
        }

        if (name == "HiveUnloadException")
        {
            return "A registry hive is still loaded, which blocks the image from dismounting. Close " +
                "regedit and any antivirus scanning the mount, then run 'reg query HKLM' to find the " +
                "zTW- mount points and 'reg unload HKLM\\<name>' on each. If that fails, reboot and run " +
                "'dism /Cleanup-Mountpoints' before building again.";
        }

        if (name == "IsoBuilderException" && MentionsMissingIsoBackend(message))
        {
            return "No ISO backend is available. Run 'tools\\fetch-xorriso.ps1' from the repository root " +
                "to download the bundled xorriso, or install the Windows ADK Deployment Tools to use " +
                "oscdimg instead.";
        }

        if (name is "DismException" or "RegistryOperationException" && MentionsAccessDenied(message))
        {
            return "Access was denied. Check that TinyWin is elevated and that no antivirus is scanning " +
                "the scratch directory — real-time scanning of a mounted image is the usual cause. " +
                "Excluding the scratch directory from scanning fixes it.";
        }

        if (exception is UnauthorizedAccessException)
        {
            return "Access was denied. Run TinyWin as Administrator, and exclude the scratch directory " +
                "from antivirus real-time scanning.";
        }

        if (exception is FileNotFoundException notFound && !string.IsNullOrEmpty(notFound.FileName))
        {
            return $"'{notFound.FileName}' was not found. Check the path, and make sure the source is " +
                "Windows 11 installation media rather than a recovery or driver disc.";
        }

        if (exception is IOException && MentionsFileInUse(message))
        {
            return "A file in the scratch directory is in use. Close any Explorer window, terminal or " +
                "editor open on it, pause antivirus scanning of that directory, and try again — the " +
                "build can be re-run with --resume.";
        }

        return null;
    }

    private static bool MentionsElevation(string message) =>
        message.Contains("740", StringComparison.Ordinal) ||
        message.Contains("elevat", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("requires elevation", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsMissingIsoBackend(string message) =>
        message.Contains("xorriso", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("oscdimg", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("backend", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsAccessDenied(string message) =>
        message.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("ERROR_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsFileInUse(string message) =>
        message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("is in use", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("sharing violation", StringComparison.OrdinalIgnoreCase);
}
