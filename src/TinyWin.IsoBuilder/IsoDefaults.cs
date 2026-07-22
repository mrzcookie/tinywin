using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder;

/// <summary>
/// The boot geometry TinyWin assumes when the source ISO was never inspected.
/// </summary>
/// <remarks>
/// <para>
/// These are not invented numbers. They were read off genuine Windows 11 25H2 (26200) media with
/// <c>xorriso -indev &lt;iso&gt; -report_el_torito plain</c>:
/// </para>
/// <code>
/// El Torito boot img :   1  BIOS  y   none  0x0000  0x00      8         534
/// El Torito boot img :   2  UEFI  y   none  0x0000  0x00      1         536
/// </code>
/// <para>
/// They are still a fallback and not a substitute for reading the source. xorriso accepts a wrong
/// <c>-boot-load-size</c> without complaint and the resulting media fails only at boot, so
/// <see cref="IsoBuilderService.ReadBootGeometryAsync"/> is the supported path and using these
/// instead is always reported as a warning rather than applied quietly.
/// </para>
/// </remarks>
public static class IsoDefaults
{
    /// <summary>Where Windows media keeps its BIOS El Torito image.</summary>
    public const string BiosBootImage = @"boot\etfsboot.com";

    /// <summary>Where Windows media keeps its UEFI El Torito image.</summary>
    public const string UefiBootImage = @"efi\microsoft\boot\efisys.bin";

    /// <summary>Sectors declared for the BIOS entry on 24H2/25H2 media. etfsboot.com is 4096 bytes.</summary>
    public const int BiosLoadSize = 8;

    /// <summary>
    /// Sectors declared for the UEFI entry on 24H2/25H2 media. Microsoft really does declare one
    /// 512-byte sector for a 1.4 MB efisys.bin — UEFI firmware reads the FAT image itself.
    /// </summary>
    public const int UefiLoadSize = 1;

    /// <summary>Volume id of English x64 24H2/25H2 retail media.</summary>
    public const string WindowsVolumeId = "CCCOMA_X64FRE_EN-US_DV9";

    /// <summary>The observed 24H2/25H2 geometry, optionally relabelled.</summary>
    public static IsoBootGeometry WindowsMediaGeometry(string? volumeId = null) => new()
    {
        VolumeId = string.IsNullOrWhiteSpace(volumeId) ? WindowsVolumeId : volumeId,
        BiosBootImage = BiosBootImage,
        BiosLoadSize = BiosLoadSize,
        UefiBootImage = UefiBootImage,
        UefiLoadSize = UefiLoadSize,
    };
}
