using System.Globalization;

namespace TinyWin.IsoBuilder;

/// <summary>Outcome of the install.wim split, including the no-op cases.</summary>
public sealed record WimSplitResult
{
    public required bool Performed { get; init; }

    /// <summary>Why nothing happened, when nothing happened. Never silently skipped.</summary>
    public string? SkipReason { get; init; }

    public IReadOnlyList<string> ProducedFiles { get; init; } = [];

    public long OriginalBytes { get; init; }
}

/// <summary>
/// Splits <c>sources\install.wim</c> into <c>install*.swm</c>.
/// </summary>
/// <remarks>
/// This is the designed escape hatch from docs/spikes/iso-build.md section 9 step 6. TinyWin's
/// output relies on ISO 9660 level 3 multi-extent to carry a >4 GiB install.wim, and Windows'
/// <c>cdfs.sys</c> was proven to read that correctly — but WinPE's boot-time reader was never
/// exercised. If a boot test ever shows Setup cannot read past 4 GiB, splitting removes the
/// requirement entirely: Windows Setup consumes <c>install.swm</c> natively.
/// </remarks>
internal static class WimSplitter
{
    /// <summary>4000 MB per part, matching the value in the spike. Comfortably under 4 GiB.</summary>
    public const int PartSizeMegabytes = 4000;

    /// <summary>The ISO 9660 single-extent ceiling. Above this, splitting is what a split is for.</summary>
    public const long SingleExtentLimitBytes = uint.MaxValue;

    public static async Task<WimSplitResult> SplitIfOversizeAsync(
        string stagedTree,
        string dismPath,
        CancellationToken cancellationToken)
    {
        var wim = Path.Combine(stagedTree, "sources", "install.wim");

        if (!File.Exists(wim))
        {
            return new WimSplitResult
            {
                Performed = false,
                SkipReason = $"'{wim}' does not exist; nothing to split.",
            };
        }

        var length = new FileInfo(wim).Length;
        if (length <= SingleExtentLimitBytes)
        {
            return new WimSplitResult
            {
                Performed = false,
                OriginalBytes = length,
                SkipReason =
                    $"install.wim is {length.ToString(CultureInfo.InvariantCulture)} bytes, within the " +
                    $"{SingleExtentLimitBytes.ToString(CultureInfo.InvariantCulture)}-byte single-extent " +
                    "limit; no split needed.",
            };
        }

        var swm = Path.Combine(stagedTree, "sources", "install.swm");

        var result = await ToolProcess.RunAsync(
            dismPath,
            [
                "/English",
                "/Split-Image",
                "/ImageFile:" + wim,
                "/SWMFile:" + swm,
                "/FileSize:" + PartSizeMegabytes.ToString(CultureInfo.InvariantCulture),
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new IsoBuilderException(
                $"dism /Split-Image failed with exit code {result.ExitCode}:" +
                Environment.NewLine + result.CombinedOutput);
        }

        var produced = Directory
            .EnumerateFiles(Path.Combine(stagedTree, "sources"), "install*.swm")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (produced.Count == 0)
        {
            throw new IsoBuilderException(
                "dism /Split-Image reported success but produced no .swm files.");
        }

        // Leaving install.wim behind would defeat the split and double the image size.
        File.SetAttributes(wim, FileAttributes.Normal);
        File.Delete(wim);

        return new WimSplitResult
        {
            Performed = true,
            OriginalBytes = length,
            ProducedFiles = produced,
        };
    }
}
