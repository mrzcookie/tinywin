using System.Globalization;
using System.Text;
using TinyWin.Core.Abstractions;

namespace TinyWin.Imaging.Dism;

/// <summary>
/// Builds <c>dism.exe</c> argument strings. Pure — no process is started here.
/// </summary>
/// <remarks>
/// Command-line construction lives behind its own seam because it is the half of
/// <see cref="DismExeBackend"/> that can be tested without elevation. Every method is a
/// deterministic string function, so the tests are golden tests: the expected argument string is
/// written out in full and compared literally.
///
/// <para><b>Every command starts with <c>/English</c>.</b> Without it DISM localises both the
/// <c>Key : Value</c> keys and the status prose, and <see cref="DismOutputParser"/> silently
/// returns nothing on a non-English host — a no-op bug of exactly the class CLAUDE.md forbids.
/// See docs/spikes/dism-backend.md §5.</para>
/// </remarks>
public static class DismCommandLine
{
    /// <summary>Forces locale-independent output. Non-negotiable — see the remarks on this type.</summary>
    public const string EnglishSwitch = "/English";

    public static string GetWimInfo(string wimPath, DismOptions? options = null) =>
        Build(options, "/Get-WimInfo", Path("/WimFile", wimPath));

    public static string GetWimInfo(string wimPath, int index, DismOptions? options = null) =>
        Build(options, "/Get-WimInfo", Path("/WimFile", wimPath), Number("/Index", index));

    public static string MountWim(string wimPath, int index, string mountPath, DismOptions? options = null) =>
        Build(options, "/Mount-Wim", Path("/WimFile", wimPath), Number("/Index", index), Path("/MountDir", mountPath));

    /// <summary>
    /// <paramref name="commit"/> false is the cancellation unwind path — it is what stops a
    /// cancelled build from leaving a half-written image behind.
    /// </summary>
    public static string UnmountWim(string mountPath, bool commit, DismOptions? options = null) =>
        Build(options, "/Unmount-Wim", Path("/MountDir", mountPath), commit ? "/Commit" : "/Discard");

    public static string GetMountedWimInfo(DismOptions? options = null) =>
        Build(options, "/Get-MountedWimInfo");

    public static string CleanupMountpoints(DismOptions? options = null) =>
        Build(options, "/Cleanup-Mountpoints");

    public static string GetProvisionedAppxPackages(string mountPath, DismOptions? options = null) =>
        BuildForImage(options, mountPath, "/Get-ProvisionedAppxPackages");

    public static string RemoveProvisionedAppxPackage(string mountPath, string packageName, DismOptions? options = null) =>
        BuildForImage(options, mountPath, "/Remove-ProvisionedAppxPackage", Text("/PackageName", packageName));

    public static string GetCapabilities(string mountPath, DismOptions? options = null) =>
        BuildForImage(options, mountPath, "/Get-Capabilities");

    public static string RemoveCapability(string mountPath, string capabilityName, DismOptions? options = null) =>
        BuildForImage(options, mountPath, "/Remove-Capability", Text("/CapabilityName", capabilityName));

    public static string GetFeatures(string mountPath, DismOptions? options = null) =>
        BuildForImage(options, mountPath, "/Get-Features");

    public static string DisableFeature(string mountPath, string featureName, bool removePayload, DismOptions? options = null) =>
        removePayload
            ? BuildForImage(options, mountPath, "/Disable-Feature", Text("/FeatureName", featureName), "/Remove")
            : BuildForImage(options, mountPath, "/Disable-Feature", Text("/FeatureName", featureName));

    public static string GetPackages(string mountPath, DismOptions? options = null) =>
        BuildForImage(options, mountPath, "/Get-Packages");

    public static string RemovePackage(string mountPath, string packageName, DismOptions? options = null) =>
        BuildForImage(options, mountPath, "/Remove-Package", Text("/PackageName", packageName));

    /// <summary>
    /// <c>/StartComponentCleanup</c>, optionally with <c>/ResetBase</c>.
    /// </summary>
    /// <remarks>
    /// This has no equivalent anywhere in <c>dismapi.dll</c> — the whole export table was checked
    /// (docs/spikes/dism-backend.md §3). This command line is not a stopgap; it is how TinyWin
    /// performs pipeline stage 9, permanently.
    ///
    /// <para><c>/ResetBase</c> makes superseded components unrecoverable, so installed updates can
    /// no longer be uninstalled. That is a deliberate size/serviceability trade the catalog surfaces
    /// to the user, not a detail to bury here.</para>
    /// </remarks>
    public static string CleanupImage(string mountPath, bool resetBase, DismOptions? options = null) =>
        resetBase
            ? BuildForImage(options, mountPath, "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase")
            : BuildForImage(options, mountPath, "/Cleanup-Image", "/StartComponentCleanup");

    /// <summary>
    /// Re-exports an image to recompress it.
    /// </summary>
    /// <remarks>
    /// Like <see cref="CleanupImage"/>, absent from <c>dismapi.dll</c> and therefore permanently
    /// exe-only. (<c>wimgapi.dll</c>'s <c>WIMExportImage</c> is a possible v2 route — a different
    /// API family, out of scope.) See docs/spikes/dism-backend.md §3.
    /// </remarks>
    public static string ExportImage(
        string sourceWimPath, int sourceIndex, string destinationWimPath, CompressionType compression,
        DismOptions? options = null) =>
        Build(
            options,
            "/Export-Image",
            Path("/SourceImageFile", sourceWimPath),
            Number("/SourceIndex", sourceIndex),
            Path("/DestinationImageFile", destinationWimPath),
            $"/Compress:{CompressionArgument(compression)}");

    /// <summary>DISM's spelling of <see cref="CompressionType"/>.</summary>
    public static string CompressionArgument(CompressionType compression) => compression switch
    {
        CompressionType.None => "none",
        CompressionType.Fast => "fast",
        CompressionType.Maximum => "max",
        // /Compress:recovery produces an ESD-style solid archive and is only valid for /Export-Image.
        CompressionType.Recovery => "recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "Unknown compression type."),
    };

    private static string BuildForImage(DismOptions? options, string mountPath, params string[] parts)
    {
        var withImage = new string[parts.Length + 1];
        withImage[0] = Path("/Image", mountPath);
        parts.CopyTo(withImage, 1);
        return Build(options, withImage, scratchApplies: true);
    }

    private static string Build(DismOptions? options, params string[] parts) =>
        Build(options, parts, scratchApplies: false);

    private static string Build(DismOptions? options, string[] parts, bool scratchApplies)
    {
        options ??= DismOptions.Default;

        var builder = new StringBuilder(EnglishSwitch);
        foreach (var part in parts)
        {
            builder.Append(' ').Append(part);
        }

        // /ScratchDir is only meaningful for image-servicing commands; appending it to, say,
        // /Get-WimInfo is how you turn a working command into an "invalid option" failure.
        if (scratchApplies && !string.IsNullOrWhiteSpace(options.ScratchDirectory))
        {
            builder.Append(' ').Append(Path("/ScratchDir", options.ScratchDirectory));
        }

        if (!string.IsNullOrWhiteSpace(options.LogPath))
        {
            builder.Append(' ').Append(Path("/LogPath", options.LogPath));
        }

        if (options.LogLevel != DismLogLevel.Default)
        {
            builder.Append(' ').Append(Number("/LogLevel", (int)options.LogLevel));
        }

        return builder.ToString();
    }

    /// <summary>Quoted because paths contain spaces far more often than not.</summary>
    private static string Path(string name, string value) => $"{name}:\"{Validate(name, value)}\"";

    /// <summary>
    /// Quoted too: capability identities and package names are user-influenced catalog data, and a
    /// quoted value is one fewer way for a stray space to turn into a different command.
    /// </summary>
    private static string Text(string name, string value) => $"{name}:\"{Validate(name, value)}\"";

    private static string Number(string name, int value) =>
        $"{name}:{value.ToString(CultureInfo.InvariantCulture)}";

    private static string Validate(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} requires a non-empty value.", nameof(value));
        }

        // Windows has no escape for a quote inside a quoted argument that DISM would honour, so a
        // value containing one cannot be passed safely. Refuse loudly rather than build a mangled
        // command line and let DISM interpret the remainder as switches.
        if (value.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{name} value contains a double quote, which cannot be passed to dism.exe safely: {value}",
                nameof(value));
        }

        return value;
    }
}
