namespace TinyWin.IsoBuilder.Tests;

public sealed class ElToritoReportParserTests
{
    [Fact]
    public void Real_media_plain_report_yields_both_load_sizes()
    {
        var report = ElToritoReportParser.ParsePlain(
            TestFiles.ReadFixture("real-25h2-report-plain.txt"));

        Assert.Equal("CCCOMA_X64FRE_EN-US_DV9", report.VolumeId);
        Assert.Equal(2, report.BootImages.Count);

        var bios = Assert.IsType<ElToritoBootImage>(report.Bios);
        Assert.Equal(8, bios.LoadSize);
        Assert.Equal(534, bios.Lba);
        Assert.True(bios.NoEmulation);

        // Microsoft declares a single 512-byte sector for the UEFI entry. Surprising, and exactly
        // why the spike forbids hardcoding this value.
        var uefi = Assert.IsType<ElToritoBootImage>(report.Uefi);
        Assert.Equal(1, uefi.LoadSize);
        Assert.Equal(536, uefi.Lba);
    }

    [Fact]
    public void Real_media_hides_its_boot_image_paths()
    {
        var report = ElToritoReportParser.ParsePlain(
            TestFiles.ReadFixture("real-25h2-report-plain.txt"));

        // Genuine Windows media keeps its boot images outside the ISO 9660 tree, so no
        // "El Torito img path" lines are emitted.
        Assert.Null(report.Bios?.ImagePath);
        Assert.Null(report.Uefi?.ImagePath);
    }

    [Fact]
    public void Real_media_as_mkisofs_report_names_no_boot_images()
    {
        // The finding that makes the plain report authoritative: as_mkisofs refuses to describe
        // boot images it cannot name, and emits nothing but -V, --boot-catalog-hide and a bare
        // -eltorito-alt-boot.
        var report = ElToritoReportParser.ParseAsMkisofs(
            TestFiles.ReadFixture("real-25h2-report-as-mkisofs.txt"));

        Assert.Equal("CCCOMA_X64FRE_EN-US_DV9", report.VolumeId);
        Assert.Empty(report.BootImages);
    }

    [Fact]
    public void Rebuilt_media_plain_report_includes_image_paths()
    {
        var report = ElToritoReportParser.ParsePlain(
            TestFiles.ReadFixture("rebuilt-report-plain.txt"));

        Assert.Equal("/boot/etfsboot.com", report.Bios?.ImagePath);
        Assert.Equal("/efi/microsoft/boot/efisys.bin", report.Uefi?.ImagePath);
        Assert.Equal(8, report.Bios?.LoadSize);

        // xorriso wrote the full efisys.bin size (1474560 / 512) despite being asked for 1.
        Assert.Equal(2880, report.Uefi?.LoadSize);
    }

    [Fact]
    public void Rebuilt_media_as_mkisofs_report_round_trips()
    {
        var report = ElToritoReportParser.ParseAsMkisofs(
            TestFiles.ReadFixture("rebuilt-report-as-mkisofs.txt"));

        Assert.Equal("CCCOMA_X64FRE_EN-US_DV9", report.VolumeId);
        Assert.Equal(2, report.BootImages.Count);

        Assert.Equal(ElToritoPlatform.Bios, report.BootImages[0].Platform);
        Assert.Equal("/boot/etfsboot.com", report.BootImages[0].ImagePath);
        Assert.Equal(8, report.BootImages[0].LoadSize);
        Assert.True(report.BootImages[0].NoEmulation);

        Assert.Equal(ElToritoPlatform.Uefi, report.BootImages[1].Platform);
        Assert.Equal("/efi/microsoft/boot/efisys.bin", report.BootImages[1].ImagePath);
        Assert.Equal(2880, report.BootImages[1].LoadSize);
    }

    [Fact]
    public void Empty_output_parses_to_an_empty_report()
    {
        var report = ElToritoReportParser.ParsePlain(string.Empty);

        Assert.Null(report.VolumeId);
        Assert.Empty(report.BootImages);
    }
}
