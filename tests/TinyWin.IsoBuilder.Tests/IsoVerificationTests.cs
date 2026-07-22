using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder.Tests;

public sealed class IsoVerificationTests
{
    private static readonly IsoBootGeometry Geometry = new()
    {
        VolumeId = "CCCOMA_X64FRE_EN-US_DV9",
        BiosBootImage = @"boot\etfsboot.com",
        BiosLoadSize = 8,
        UefiBootImage = @"efi\microsoft\boot\efisys.bin",
        UefiLoadSize = 1,
    };

    private static ElToritoReport GoodCatalog(int biosLoadSize = 8, int uefiLoadSize = 2880) =>
        new()
        {
            VolumeId = "CCCOMA_X64FRE_EN-US_DV9",
            BootImages =
            [
                new ElToritoBootImage
                {
                    Index = 1,
                    Platform = ElToritoPlatform.Bios,
                    LoadSize = biosLoadSize,
                    ImagePath = "/boot/etfsboot.com",
                },
                new ElToritoBootImage
                {
                    Index = 2,
                    Platform = ElToritoPlatform.Uefi,
                    LoadSize = uefiLoadSize,
                    ImagePath = "/efi/microsoft/boot/efisys.bin",
                },
            ],
        };

    [Fact]
    public void A_matching_image_passes()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile(Path.Combine("boot", "etfsboot.com"), 4096);
        tree.WriteFile("setup.exe", 99896);

        var result = IsoVerification.Compare(
            [
                new IsoFileEntry(@"boot\etfsboot.com", 4096),
                new IsoFileEntry("setup.exe", 99896),
            ],
            tree.Path,
            GoodCatalog(),
            Geometry);

        Assert.True(result.Passed);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.FilesInImage);
        Assert.Equal(2, result.FilesInSource);
    }

    [Fact]
    public void A_file_missing_from_the_image_is_an_error()
    {
        // What a Joliet truncation looks like from the outside: the file is simply not there
        // under the name it was staged with.
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile("setup.exe", 99896);
        tree.WriteFile(Path.Combine("sources", "install.wim"), 4096);

        var result = IsoVerification.Compare(
            [new IsoFileEntry("setup.exe", 99896)],
            tree.Path,
            GoodCatalog(),
            Geometry);

        Assert.False(result.Passed);
        Assert.Contains(result.Errors, e => e.Contains("install.wim", StringComparison.Ordinal));
    }

    [Fact]
    public void A_truncated_file_is_an_error()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile(Path.Combine("sources", "install.wim"), 5_000_000_000);

        var result = IsoVerification.Compare(
            [new IsoFileEntry(@"sources\install.wim", 4_294_967_295)],
            tree.Path,
            GoodCatalog(),
            Geometry);

        Assert.False(result.Passed);
        Assert.Contains(result.Errors, e => e.Contains("install.wim", StringComparison.Ordinal));
    }

    [Fact]
    public void An_extra_file_in_the_image_is_only_a_warning()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile("setup.exe", 8);

        var result = IsoVerification.Compare(
            [new IsoFileEntry("setup.exe", 8), new IsoFileEntry("boot.catalog", 2048)],
            tree.Path,
            GoodCatalog(),
            Geometry);

        Assert.True(result.Passed);
        Assert.Contains(result.Warnings, w => w.Contains("boot.catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrong_bios_load_size_is_an_error()
    {
        // xorriso accepts a wrong load size silently, so this is the only place it gets caught
        // before someone tries to boot the media.
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile("setup.exe", 8);

        var result = IsoVerification.Compare(
            [new IsoFileEntry("setup.exe", 8)],
            tree.Path,
            GoodCatalog(biosLoadSize: 4),
            Geometry);

        Assert.False(result.Passed);
        Assert.Contains(result.Errors, e => e.Contains("BIOS boot load size", StringComparison.Ordinal));
    }

    [Fact]
    public void The_uefi_load_size_xorriso_forces_is_reported_as_a_warning()
    {
        // Established against real media: xorriso always writes the full EFI image size for the -e
        // entry and ignores -boot-load-size, whatever the option order. Microsoft declares 1.
        // Reported rather than swallowed, but not a failure - full size is what Linux ISOs ship.
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile("setup.exe", 8);

        var result = IsoVerification.Compare(
            [new IsoFileEntry("setup.exe", 8)],
            tree.Path,
            GoodCatalog(uefiLoadSize: 2880),
            Geometry);

        Assert.True(result.Passed);
        Assert.Contains(result.Warnings, w => w.Contains("UEFI boot load size", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_boot_entry_is_an_error()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile("setup.exe", 8);

        var uefiOnly = new ElToritoReport
        {
            BootImages =
            [
                new ElToritoBootImage { Index = 1, Platform = ElToritoPlatform.Uefi, LoadSize = 2880 },
            ],
        };

        var result = IsoVerification.Compare(
            [new IsoFileEntry("setup.exe", 8)], tree.Path, uefiOnly, Geometry);

        Assert.False(result.Passed);
        Assert.Contains(result.Errors, e => e.Contains("BIOS", StringComparison.Ordinal));
    }

    [Fact]
    public void An_emulated_boot_entry_is_an_error()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile("setup.exe", 8);

        var catalog = GoodCatalog() with
        {
            BootImages =
            [
                new ElToritoBootImage
                {
                    Index = 1,
                    Platform = ElToritoPlatform.Bios,
                    LoadSize = 8,
                    NoEmulation = false,
                },
                new ElToritoBootImage { Index = 2, Platform = ElToritoPlatform.Uefi, LoadSize = 2880 },
            ],
        };

        var result = IsoVerification.Compare(
            [new IsoFileEntry("setup.exe", 8)], tree.Path, catalog, Geometry);

        Assert.False(result.Passed);
        Assert.Contains(result.Errors, e => e.Contains("no-emulation", StringComparison.Ordinal));
    }
}
