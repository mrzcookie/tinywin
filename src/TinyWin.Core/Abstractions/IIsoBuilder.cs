namespace TinyWin.Core.Abstractions;

/// <summary>Which tool actually writes the ISO. See docs/PLAN.md section 3.1.</summary>
public enum IsoBackendKind
{
    /// <summary>Bundled xorriso. Primary — GPLv3 lets us ship it, so the app needs no ADK.</summary>
    Xorriso,

    /// <summary>oscdimg.exe from a detected Windows ADK install. Fallback.</summary>
    Oscdimg,
}

public sealed record IsoBackendAvailability(
    IsoBackendKind Kind,
    bool Available,
    string? ExecutablePath,
    string? Version,
    string? UnavailableReason);

/// <summary>Everything needed to write a bootable Windows installation ISO.</summary>
public sealed record IsoBuildRequest
{
    /// <summary>Directory holding the full ISO tree to be packaged.</summary>
    public required string SourceDirectory { get; init; }

    public required string OutputIsoPath { get; init; }

    /// <summary>Volume label. Windows setup does not require a specific value.</summary>
    public string VolumeLabel { get; init; } = "TINYWIN";

    /// <summary>BIOS El Torito boot image, relative to <see cref="SourceDirectory"/>.</summary>
    public string BiosBootImage { get; init; } = @"boot\etfsboot.com";

    /// <summary>UEFI El Torito boot image, relative to <see cref="SourceDirectory"/>.</summary>
    public string UefiBootImage { get; init; } = @"efi\microsoft\boot\efisys.bin";

    /// <summary>
    /// Boot geometry read from the source ISO via <c>xorriso -report_el_torito as_mkisofs</c>.
    /// Never hardcode these — xorriso silently accepts a wrong load size, producing media that
    /// fails only at boot. Null means "read it during Inspect".
    /// </summary>
    public IsoBootGeometry? BootGeometry { get; init; }

    /// <summary>
    /// Split install.wim into install.swm when it exceeds 4 GiB, instead of relying on ISO 9660
    /// level 3 multi-extent. The escape hatch if WinPE turns out not to read multi-extent files —
    /// see docs/spikes/iso-build.md section 2b.
    /// </summary>
    public bool SplitOversizeImage { get; init; }
}

/// <summary>
/// El Torito boot catalog values lifted from the source ISO, so the rebuilt image reproduces
/// them exactly rather than guessing.
/// </summary>
public sealed record IsoBootGeometry
{
    public required string VolumeId { get; init; }
    public required string BiosBootImage { get; init; }
    public required int BiosLoadSize { get; init; }
    public required string UefiBootImage { get; init; }
    public required int UefiLoadSize { get; init; }
}

public interface IIsoBuilder
{
    /// <summary>Reports which backends are usable on this machine, for settings and diagnostics.</summary>
    Task<IReadOnlyList<IsoBackendAvailability>> ProbeBackendsAsync(
        CancellationToken cancellationToken = default);

    Task BuildAsync(
        IsoBuildRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Extracts an ISO's full contents to a working directory.</summary>
    Task ExtractAsync(
        string isoPath,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
