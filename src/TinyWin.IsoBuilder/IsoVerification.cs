using System.Globalization;
using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder;

/// <summary>What post-build verification found.</summary>
public sealed record IsoVerificationResult
{
    public required bool Passed { get; init; }

    /// <summary>Problems that make the image unusable. Non-empty means the build failed.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>Deviations worth reporting that do not make the image unusable.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public required int FilesInImage { get; init; }

    public required int FilesInSource { get; init; }

    public ElToritoReport? Catalog { get; init; }

    public string Describe()
    {
        var lines = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"{FilesInImage} file(s) in the image, {FilesInSource} in the staged tree."),
        };

        lines.AddRange(Errors.Select(e => "ERROR: " + e));
        lines.AddRange(Warnings.Select(w => "WARNING: " + w));

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Compares a finished image against the tree it was built from, and against the boot geometry it
/// was supposed to reproduce.
/// </summary>
/// <remarks>
/// The point is to catch a bad image here rather than at boot. Two specific failures this is aimed
/// at: a name Joliet truncated (the file goes missing under a different name) and a member file
/// written short (the multi-extent path failing on a >4 GiB install.wim).
/// </remarks>
public static class IsoVerification
{
    public static IsoVerificationResult Compare(
        IReadOnlyList<IsoFileEntry> imageFiles,
        string stagedTree,
        ElToritoReport catalog,
        IsoBootGeometry expectedGeometry)
    {
        ArgumentNullException.ThrowIfNull(imageFiles);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(expectedGeometry);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedTree);

        var errors = new List<string>();
        var warnings = new List<string>();

        var inImage = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in imageFiles)
        {
            inImage[entry.Path] = entry.Length;
        }

        var root = new DirectoryInfo(stagedTree);
        var sourceCount = 0;

        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            sourceCount++;
            var relative = Path.GetRelativePath(root.FullName, file.FullName);

            if (!inImage.TryGetValue(relative, out var lengthInImage))
            {
                errors.Add($"'{relative}' is in the staged tree but not in the image.");
                continue;
            }

            if (lengthInImage != file.Length)
            {
                errors.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{relative}' is {file.Length} bytes in the staged tree but {lengthInImage} in the image."));
            }

            inImage.Remove(relative);
        }

        foreach (var extra in inImage.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"'{extra}' is in the image but not in the staged tree.");
        }

        VerifyCatalog(catalog, expectedGeometry, errors, warnings);

        return new IsoVerificationResult
        {
            Passed = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            FilesInImage = imageFiles.Count,
            FilesInSource = sourceCount,
            Catalog = catalog,
        };
    }

    private static void VerifyCatalog(
        ElToritoReport catalog,
        IsoBootGeometry expected,
        List<string> errors,
        List<string> warnings)
    {
        var bios = catalog.Bios;
        var uefi = catalog.Uefi;

        if (bios is null)
        {
            errors.Add("The image has no BIOS (platform 0x00) El Torito entry; it will not boot on Gen 1 / CSM.");
        }
        else
        {
            if (!bios.NoEmulation)
            {
                errors.Add("The BIOS boot entry is not marked no-emulation.");
            }

            if (bios.LoadSize != expected.BiosLoadSize)
            {
                errors.Add(
                    $"BIOS boot load size is {bios.LoadSize.ToString(CultureInfo.InvariantCulture)} but " +
                    $"the source ISO declares {expected.BiosLoadSize.ToString(CultureInfo.InvariantCulture)}. " +
                    "xorriso accepts a wrong load size silently, so this must not be ignored.");
            }
        }

        if (uefi is null)
        {
            errors.Add("The image has no UEFI (platform 0xEF) El Torito entry; it will not boot on Gen 2.");
        }
        else
        {
            if (!uefi.NoEmulation)
            {
                errors.Add("The UEFI boot entry is not marked no-emulation.");
            }

            if (uefi.LoadSize != expected.UefiLoadSize)
            {
                // Established against real 25H2 media: xorriso always writes the full size of the
                // EFI boot image and ignores -boot-load-size for the -e entry, whatever the option
                // order. Microsoft's own media declares 1 sector. Reported rather than swallowed,
                // but not an error - the full-size form is what every Linux ISO ships.
                warnings.Add(
                    $"UEFI boot load size is {uefi.LoadSize.ToString(CultureInfo.InvariantCulture)}, not the " +
                    $"{expected.UefiLoadSize.ToString(CultureInfo.InvariantCulture)} declared by the source " +
                    "ISO. xorriso always writes the full EFI image size and ignores -boot-load-size for " +
                    "the -e entry.");
            }
        }

        if (catalog.BootImages.Count > 2)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The boot catalog has {catalog.BootImages.Count} entries; Windows media has 2."));
        }
    }
}
