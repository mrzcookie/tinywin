namespace TinyWin.IsoBuilder.Tests;

/// <summary>
/// Runs against a real Windows 11 ISO the developer already owns.
/// </summary>
/// <remarks>
/// Set <c>TINYWIN_TEST_ISO</c> to the path of a 24H2/25H2 ISO to enable these. They are skipped
/// otherwise: cloud CI has no Windows media and TinyWin never downloads any. Nothing here writes
/// to the source ISO — it is opened read-only and, when mounted, attached with
/// ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY.
/// </remarks>
public sealed class RealMediaTests
{
    private const string IsoVariable = "TINYWIN_TEST_ISO";

    [Fact]
    public async Task Boot_geometry_comes_off_real_media_intact()
    {
        var iso = RequireIso();
        await SkipIfNoXorrisoAsync();

        var service = new IsoBuilderService();
        var inspection = await service.InspectAsync(iso, TestContext.Current.CancellationToken);
        var geometry = inspection.Geometry;

        // Every field must be populated: a zero load size is the failure mode that produces media
        // which fails only at boot.
        Assert.False(string.IsNullOrWhiteSpace(geometry.VolumeId));
        Assert.True(geometry.BiosLoadSize > 0, "BIOS load size was not read from the source.");
        Assert.True(geometry.UefiLoadSize > 0, "UEFI load size was not read from the source.");
        Assert.Equal(@"boot\etfsboot.com", geometry.BiosBootImage);
        Assert.Equal(@"efi\microsoft\boot\efisys.bin", geometry.UefiBootImage);

        // Both entries exist in the plain report even though as_mkisofs cannot name them.
        Assert.Equal(2, inspection.PlainReport.BootImages.Count);
        Assert.Empty(inspection.AsMkisofsReport.BootImages);

        // Microsoft media hides its boot images from the ISO 9660 tree, so the paths were assumed.
        // That assumption is recorded rather than made quietly.
        Assert.Equal(2, inspection.Notes.Count);
        Assert.All(inspection.Notes, note => Assert.Contains("hides", note, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retail_25H2_media_declares_the_documented_load_sizes()
    {
        var iso = RequireIso();
        await SkipIfNoXorrisoAsync();

        var geometry = await new IsoBuilderService()
            .ReadBootGeometryAsync(iso, TestContext.Current.CancellationToken);

        if (!string.Equals(geometry.VolumeId, IsoDefaults.WindowsVolumeId, StringComparison.Ordinal))
        {
            Assert.Skip($"'{geometry.VolumeId}' is not English x64 retail media; load sizes may differ.");
        }

        Assert.Equal(IsoDefaults.BiosLoadSize, geometry.BiosLoadSize);
        Assert.Equal(IsoDefaults.UefiLoadSize, geometry.UefiLoadSize);
    }

    [Fact]
    public void A_windows_iso_can_be_mounted_read_only_without_elevation()
    {
        var iso = RequireIso();

        using var mount = IsoImageMount.Attach(iso, TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(mount.RootPath));

        // The content xorriso cannot see: real Windows media keeps everything in the UDF tree,
        // where the ISO 9660 tree holds only README.TXT.
        var wim = Path.Combine(mount.RootPath, "sources", "install.wim");
        Assert.True(File.Exists(wim), $"'{wim}' should exist on Windows installation media.");
        Assert.True(new FileInfo(wim).Length > 0);
    }

    [Fact]
    public async Task Xorriso_cannot_read_a_windows_iso_tree()
    {
        // Documents the reason ExtractAsync mounts instead of using xorriso. If this ever starts
        // returning the real tree, extraction could be simplified.
        var iso = RequireIso();
        await SkipIfNoXorrisoAsync();

        var service = new IsoBuilderService();
        var geometry = await service.ReadBootGeometryAsync(iso, TestContext.Current.CancellationToken);

        using var empty = TestFiles.NewTempDirectory();
        var verification = await service.VerifyAsync(
            iso, empty.Path, geometry, TestContext.Current.CancellationToken);

        // Against an empty staged tree, every file xorriso *can* see shows up as an extra. On
        // Microsoft media that is README.TXT and nothing else.
        Assert.True(verification.FilesInImage <= 1, "xorriso unexpectedly read the UDF tree.");
    }

    private static string RequireIso()
    {
        var iso = Environment.GetEnvironmentVariable(IsoVariable);

        if (string.IsNullOrWhiteSpace(iso) || !File.Exists(iso))
        {
            Assert.Skip($"Set {IsoVariable} to a Windows 11 24H2/25H2 ISO to run the real-media tests.");
        }

        return iso;
    }

    private static async Task SkipIfNoXorrisoAsync()
    {
        var backends = await new IsoBuilderService().ProbeBackendsAsync(TestContext.Current.CancellationToken);

        if (!backends.Single(b => b.Kind == Core.Abstractions.IsoBackendKind.Xorriso).Available)
        {
            Assert.Skip("xorriso is not vendored here; run tools/fetch-xorriso.ps1.");
        }
    }
}
