using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder;

/// <summary>
/// Builds the ADK <c>oscdimg.exe</c> command line — the fallback backend, and the control build
/// for the boot-test matrix in docs/spikes/iso-build.md section 9.
/// </summary>
/// <remarks>
/// This reproduces tiny11builder's invocation, which is Microsoft's own documented form. Unlike
/// xorriso, oscdimg writes UDF 1.02, so it escapes the 4 GiB single-extent ceiling the way
/// Microsoft's own media does and needs no load sizes — it derives them from the boot images.
/// oscdimg takes native Windows paths, so nothing here goes through <see cref="CygwinPath"/>.
/// </remarks>
public static class OscdimgCommandLine
{
    public static IReadOnlyList<string> BuildArguments(IsoBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceDirectory));
        var bios = Path.Combine(source, NormalizeRelative(request.BiosBootImage));
        var uefi = Path.Combine(source, NormalizeRelative(request.UefiBootImage));

        return
        [
            "-m",           // no image-size limit
            "-o",           // dedupe identical files
            "-u2",          // UDF 1.02 …
            "-udfver102",   // … which is what holds install.wim past 4 GiB
            "-l" + request.VolumeLabel,

            // 2 boot entries: platform 0 (BIOS) and platform 0xEF (UEFI), both no-emulation.
            $"-bootdata:2#p0,e,b{bios}#pEF,e,b{uefi}",

            source,
            Path.GetFullPath(request.OutputIsoPath),
        ];
    }

    /// <summary>
    /// The geometry read from the source ISO, expressed as the boot images oscdimg should use.
    /// oscdimg has no equivalent of <c>-boot-load-size</c>, so the load sizes are not passed on.
    /// </summary>
    public static IsoBuildRequest ApplyGeometry(IsoBuildRequest request, IsoBootGeometry? geometry)
    {
        ArgumentNullException.ThrowIfNull(request);

        return geometry is null
            ? request
            : request with
            {
                BiosBootImage = geometry.BiosBootImage,
                UefiBootImage = geometry.UefiBootImage,
            };
    }

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
}
