namespace TinyWin.App.Services;

/// <summary>
/// Turns a component's uncompressed <c>estimatedSavingsMb</c> into an estimated ISO size.
/// </summary>
/// <remarks>
/// The catalog's figure is uncompressed payload — what the files weigh inside the mounted image.
/// The ISO stores them compressed, so removing 1 GB of files does not shrink the media by 1 GB.
/// Applying the factor here rather than in the catalog data is deliberate: the factor is a property
/// of how the image is exported (<c>/Compress:recovery</c>), not of any component.
///
/// TEMPORARY. This belongs in TinyWin.Core so the CLI's build report and this readout cannot
/// disagree — two heads on one engine should not each carry their own copy of the constant. When
/// Core exposes the helper, delete this type and forward to it; nothing outside this file needs to
/// change.
/// </remarks>
public static class SizeEstimator
{
    /// <summary>
    /// Compressed-to-uncompressed ratio for recovery-compressed install media. A rough figure, and
    /// treated as one — every readout built on it is labelled an estimate.
    /// </summary>
    private const double CompressionFactor = 0.40;

    private const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>How much smaller the output ISO is likely to be, in bytes.</summary>
    public static long IsoSavingsBytes(int uncompressedMegabytes) =>
        (long)(uncompressedMegabytes * BytesPerMegabyte * CompressionFactor);

    /// <summary>Estimated output ISO size. Never negative, never larger than the source.</summary>
    public static long EstimatedOutputBytes(long sourceBytes, int uncompressedMegabytes) =>
        Math.Clamp(sourceBytes - IsoSavingsBytes(uncompressedMegabytes), 0, sourceBytes);
}
