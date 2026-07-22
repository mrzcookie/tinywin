using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TinyWin.IsoBuilder;

/// <summary>El Torito platform id, as xorriso names it.</summary>
public enum ElToritoPlatform
{
    Unknown,

    /// <summary>Platform 0x00 — x86 BIOS. oscdimg calls this <c>p0</c>.</summary>
    Bios,

    /// <summary>Platform 0xEF — UEFI. oscdimg calls this <c>pEF</c>.</summary>
    Uefi,
}

/// <summary>One entry in an image's El Torito boot catalog.</summary>
public sealed record ElToritoBootImage
{
    public required int Index { get; init; }

    public required ElToritoPlatform Platform { get; init; }

    /// <summary>Sectors of 512 bytes the firmware is told to load.</summary>
    public required int LoadSize { get; init; }

    public long Lba { get; init; }

    /// <summary>
    /// Path inside the ISO 9660 tree, or null when the boot image is hidden — which is the case on
    /// genuine Microsoft media, where it lives outside the ISO 9660 tree entirely.
    /// </summary>
    public string? ImagePath { get; init; }

    public bool NoEmulation { get; init; } = true;
}

/// <summary>Parsed output of <c>xorriso -report_el_torito</c>.</summary>
public sealed record ElToritoReport
{
    public string? VolumeId { get; init; }

    public required IReadOnlyList<ElToritoBootImage> BootImages { get; init; }

    public ElToritoBootImage? Bios => BootImages.FirstOrDefault(i => i.Platform == ElToritoPlatform.Bios);

    public ElToritoBootImage? Uefi => BootImages.FirstOrDefault(i => i.Platform == ElToritoPlatform.Uefi);
}

/// <summary>
/// Parses both flavours of <c>xorriso -report_el_torito</c> output.
/// </summary>
/// <remarks>
/// Both are needed, and the reason is a real-media finding the spike's synthetic tree could not
/// show. On a genuine Windows 11 ISO the boot images are not files in the ISO 9660 tree, so
/// <c>as_mkisofs</c> refuses to name them and emits nothing but <c>-V</c>,
/// <c>--boot-catalog-hide</c> and a bare <c>-eltorito-alt-boot</c>. <c>plain</c> still reports both
/// catalog entries with their load sizes, so it is the authoritative source for geometry;
/// <c>as_mkisofs</c> contributes image paths on the media where they exist.
/// </remarks>
public static partial class ElToritoReportParser
{
    public static ElToritoReport ParsePlain(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string? volumeId = null;
        var images = new Dictionary<int, ElToritoBootImage>();

        foreach (var line in SplitLines(output))
        {
            var volume = VolumeIdPattern().Match(line);
            if (volume.Success)
            {
                volumeId = volume.Groups["id"].Value;
                continue;
            }

            var img = BootImagePattern().Match(line);
            if (img.Success)
            {
                var index = int.Parse(img.Groups["n"].Value, CultureInfo.InvariantCulture);
                images[index] = new ElToritoBootImage
                {
                    Index = index,
                    Platform = ParsePlatform(img.Groups["platform"].Value),
                    LoadSize = int.Parse(img.Groups["ldsiz"].Value, CultureInfo.InvariantCulture),
                    Lba = long.Parse(img.Groups["lba"].Value, CultureInfo.InvariantCulture),
                    NoEmulation = string.Equals(img.Groups["emul"].Value, "none", StringComparison.Ordinal),
                };
                continue;
            }

            var path = ImagePathPattern().Match(line);
            if (path.Success)
            {
                var index = int.Parse(path.Groups["n"].Value, CultureInfo.InvariantCulture);
                if (images.TryGetValue(index, out var existing))
                {
                    images[index] = existing with { ImagePath = path.Groups["path"].Value.Trim() };
                }
            }
        }

        return new ElToritoReport
        {
            VolumeId = volumeId,
            BootImages = [.. images.Values.OrderBy(i => i.Index)],
        };
    }

    public static ElToritoReport ParseAsMkisofs(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string? volumeId = null;
        var images = new List<ElToritoBootImage>();
        string? pendingPath = null;
        var pendingPlatform = ElToritoPlatform.Unknown;
        var pendingNoEmul = false;
        int? pendingLoadSize = null;

        void Flush()
        {
            if (pendingPath is null)
            {
                return;
            }

            images.Add(new ElToritoBootImage
            {
                Index = images.Count + 1,
                Platform = pendingPlatform,
                LoadSize = pendingLoadSize ?? 0,
                ImagePath = pendingPath,
                NoEmulation = pendingNoEmul,
            });

            pendingPath = null;
            pendingPlatform = ElToritoPlatform.Unknown;
            pendingNoEmul = false;
            pendingLoadSize = null;
        }

        foreach (var raw in SplitLines(output))
        {
            var line = raw.Trim();

            if (TryReadOption(line, "-V", out var volume))
            {
                volumeId = Unquote(volume);
            }
            else if (TryReadOption(line, "-b", out var bios))
            {
                Flush();
                pendingPath = Unquote(bios);
                pendingPlatform = ElToritoPlatform.Bios;
            }
            else if (TryReadOption(line, "-e", out var uefi))
            {
                Flush();
                pendingPath = Unquote(uefi);
                pendingPlatform = ElToritoPlatform.Uefi;
            }
            else if (TryReadOption(line, "-boot-load-size", out var loadSize) &&
                     int.TryParse(Unquote(loadSize), CultureInfo.InvariantCulture, out var parsed))
            {
                pendingLoadSize = parsed;
            }
            else if (string.Equals(line, "-no-emul-boot", StringComparison.Ordinal))
            {
                pendingNoEmul = true;
            }
            else if (string.Equals(line, "-eltorito-alt-boot", StringComparison.Ordinal))
            {
                Flush();
            }
        }

        Flush();

        return new ElToritoReport { VolumeId = volumeId, BootImages = images };
    }

    private static ElToritoPlatform ParsePlatform(string value) => value.ToUpperInvariant() switch
    {
        "BIOS" => ElToritoPlatform.Bios,
        "UEFI" => ElToritoPlatform.Uefi,
        _ => ElToritoPlatform.Unknown,
    };

    private static bool TryReadOption(string line, string option, [NotNullWhen(true)] out string? value)
    {
        if (line.StartsWith(option + " ", StringComparison.Ordinal))
        {
            value = line[(option.Length + 1)..].Trim();
            return value.Length > 0;
        }

        value = null;
        return false;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' ? value[1..^1] : value;

    private static string[] SplitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"^Volume id\s*:\s*'(?<id>.*)'\s*$", RegexOptions.ExplicitCapture)]
    private static partial Regex VolumeIdPattern();

    [GeneratedRegex(
        @"^El Torito boot img\s*:\s*(?<n>\d+)\s+(?<platform>\S+)\s+(?<bootable>\S+)\s+(?<emul>\S+)\s+" +
        @"(?<ldseg>\S+)\s+(?<hdpt>\S+)\s+(?<ldsiz>\d+)\s+(?<lba>\d+)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex BootImagePattern();

    [GeneratedRegex(@"^El Torito img path\s*:\s*(?<n>\d+)\s+(?<path>.+)$", RegexOptions.ExplicitCapture)]
    private static partial Regex ImagePathPattern();
}
