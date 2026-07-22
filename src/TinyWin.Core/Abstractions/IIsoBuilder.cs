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
    /// Always true in practice: install.wim exceeds 4 GB on real media (7.06 GB on 25H2), which
    /// ISO9660 cannot represent. Kept explicit so the requirement is visible rather than implied.
    /// </summary>
    public bool RequireUdf { get; init; } = true;
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
