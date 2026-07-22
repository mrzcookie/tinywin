using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder;

/// <summary>Which flavour of <c>-report_el_torito</c> output to ask xorriso for.</summary>
public enum ElToritoReportFormat
{
    /// <summary>The human-readable catalog dump. The only form that survives hidden boot images.</summary>
    Plain,

    /// <summary>The reproduce-this-image option list. Empty on media whose boot images are hidden.</summary>
    AsMkisofs,
}

/// <summary>
/// Builds xorriso command lines. Pure functions, no I/O — the build argument list is covered by
/// a golden test against checked-in expected output (docs/PLAN.md section 5).
/// </summary>
/// <remarks>
/// The build line is the one from docs/spikes/iso-build.md section 8. Two families of options are
/// deliberately absent and must stay absent:
/// <list type="bullet">
/// <item><c>-boot-info-table</c> rewrites bytes <em>inside</em> the boot image. It is a SYSLINUX
/// patching feature and must never touch Microsoft's <c>etfsboot.com</c>.</item>
/// <item><c>-isohybrid-*</c> is a no-op without a SYSLINUX <c>isohdpfx.bin</c>, and produces no
/// system area at all on its own — verified in spike section 7.</item>
/// </list>
/// </remarks>
public static class XorrisoCommandLine
{
    /// <summary>
    /// Raises xorriso's exit-code threshold so only FAILURE and worse set a non-zero code.
    /// </summary>
    /// <remarks>
    /// Genuine Microsoft media reports two SORRY events ("Cannot enable EL Torito boot image
    /// because it is not a data file in the ISO filesystem") because its boot images live outside
    /// the ISO 9660 tree. Without this, reading a perfectly good 25H2 ISO exits 32.
    /// </remarks>
    private static readonly string[] ReturnWithFailure = ["-return_with", "FAILURE", "32"];

    /// <summary>
    /// The full <c>xorrisofs</c> argument list for writing TinyWin's output image.
    /// </summary>
    /// <param name="request">Source tree, output path and volume label.</param>
    /// <param name="geometry">
    /// Boot geometry read from the user's source ISO. Required: xorriso silently accepts a wrong
    /// <c>-boot-load-size</c>, which yields media that fails only at boot, so this is never guessed.
    /// </param>
    public static IReadOnlyList<string> BuildArguments(IsoBuildRequest request, IsoBootGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.BiosLoadSize <= 0 || geometry.UefiLoadSize <= 0)
        {
            throw new IsoBuilderException(
                "Boot load sizes must be positive. They are read from the source ISO with " +
                "-report_el_torito, never hardcoded.");
        }

        return
        [
            .. ReturnWithFailure,
            "-as", "mkisofs",

            // Level 3 permits multi-extent files, which is what carries a >4 GiB install.wim.
            // It replaces oscdimg's "-u2 -udfver102"; xorriso cannot write UDF at all.
            "-iso-level", "3",

            // With UDF gone, Joliet is what carries exact Windows filenames. Not optional:
            // the alternatives silently truncate at 31 and 37 characters.
            "-J", "-joliet-long",

            "-volid", request.VolumeLabel,

            // Parity with oscdimg, which leaves no boot.catalog in either name tree.
            "-hide", "boot.catalog",
            "-hide-joliet", "boot.catalog",

            // Catalog entry 1 — platform 0x00 (x86 BIOS), no emulation.
            "-b", CygwinPath.ToIsoRelative(geometry.BiosBootImage),
            "-no-emul-boot",
            "-boot-load-size", geometry.BiosLoadSize.ToString(System.Globalization.CultureInfo.InvariantCulture),

            // Catalog entry 2 — -e marks platform 0xEF (UEFI) automatically.
            "-eltorito-alt-boot",
            "-e", CygwinPath.ToIsoRelative(geometry.UefiBootImage),
            "-no-emul-boot",
            "-boot-load-size", geometry.UefiLoadSize.ToString(System.Globalization.CultureInfo.InvariantCulture),

            // Note the reversed order relative to oscdimg: output first, then the tree.
            "-o", CygwinPath.FromWindows(request.OutputIsoPath),
            CygwinPath.FromWindows(request.SourceDirectory),
        ];
    }

    /// <summary>Reads an existing image's El Torito catalog.</summary>
    public static IReadOnlyList<string> ReportElToritoArguments(string isoPath, ElToritoReportFormat format) =>
    [
        .. ReturnWithFailure,
        "-indev", CygwinPath.FromWindows(isoPath),
        "-report_el_torito", format == ElToritoReportFormat.Plain ? "plain" : "as_mkisofs",
    ];

    /// <summary>
    /// Lists every file in an image with its size, so a truncated name or a dropped file is caught
    /// before the user tries to boot the media.
    /// </summary>
    /// <remarks>
    /// <c>-find / -type f -exec lsdl</c> is the recursive form of <c>-lsl</c>: same formatter, whole
    /// tree, absolute paths. That is what makes a file-for-file comparison against the staged tree
    /// possible without mounting the image.
    /// </remarks>
    public static IReadOnlyList<string> ListAllFilesArguments(string isoPath) =>
    [
        .. ReturnWithFailure,
        "-indev", CygwinPath.FromWindows(isoPath),
        "-find", "/", "-type", "f", "-exec", "lsdl", "--",
    ];

    /// <summary>Lists one directory of an image, in <c>ls -l</c> form.</summary>
    public static IReadOnlyList<string> ListDirectoryArguments(string isoPath, string isoDirectory) =>
    [
        .. ReturnWithFailure,
        "-indev", CygwinPath.FromWindows(isoPath),
        "-lsl", isoDirectory,
    ];

    /// <summary>Prints the version banner. Used by backend probing.</summary>
    public static IReadOnlyList<string> VersionArguments() => ["-version"];
}
