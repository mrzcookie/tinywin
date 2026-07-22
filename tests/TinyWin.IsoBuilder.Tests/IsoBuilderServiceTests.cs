using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder.Tests;

/// <summary>
/// Exercises the real xorriso binary end to end on a synthetic tree.
/// </summary>
/// <remarks>
/// These skip rather than fail when the vendored bundle is absent, because it is fetched by
/// tools/fetch-xorriso.ps1 rather than committed. CI runs that script before dotnet test; a
/// developer who has not may still run everything else.
/// </remarks>
public sealed class IsoBuilderServiceTests
{
    [Fact]
    public async Task Probe_reports_both_backends()
    {
        var backends = await new IsoBuilderService().ProbeBackendsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, backends.Count);
        Assert.Contains(backends, b => b.Kind == IsoBackendKind.Xorriso);
        Assert.Contains(backends, b => b.Kind == IsoBackendKind.Oscdimg);

        foreach (var backend in backends)
        {
            // Every unavailable backend has to say why. A silent false is exactly the kind of
            // no-op CLAUDE.md forbids.
            Assert.True(
                backend.Available || !string.IsNullOrWhiteSpace(backend.UnavailableReason),
                $"{backend.Kind} is unavailable but gave no reason.");

            Assert.True(
                !backend.Available || !string.IsNullOrWhiteSpace(backend.ExecutablePath),
                $"{backend.Kind} is available but gave no path.");
        }
    }

    [Fact]
    public async Task Probe_reads_the_vendored_xorriso_version()
    {
        var backends = await new IsoBuilderService().ProbeBackendsAsync(TestContext.Current.CancellationToken);
        var xorriso = backends.Single(b => b.Kind == IsoBackendKind.Xorriso);

        if (!xorriso.Available)
        {
            Assert.Skip("xorriso is not vendored here; run tools/fetch-xorriso.ps1.");
        }

        Assert.Matches(@"^\d+\.\d+\.\d+$", xorriso.Version);
    }

    [Fact]
    public async Task Building_a_synthetic_tree_produces_a_verifiable_dual_boot_image()
    {
        await SkipIfNoXorrisoAsync();

        using var work = TestFiles.NewTempDirectory();
        var tree = Path.Combine(work.Path, "iso");
        var output = Path.Combine(work.Path, "tinywin.iso");

        // 4096 bytes = 8 sectors, which is what real etfsboot.com measures.
        WriteBootImage(Path.Combine(tree, "boot", "etfsboot.com"), 4096);
        WriteBootImage(Path.Combine(tree, "efi", "microsoft", "boot", "efisys.bin"), 1_474_560);
        WriteText(Path.Combine(tree, "setup.exe"), "not really setup");
        WriteText(
            Path.Combine(tree, "sources", "api-ms-win-core-processthreads-l1-1-2.dll"),
            "long name, exactly preserved");

        var geometry = new IsoBootGeometry
        {
            VolumeId = "TINYWIN_TEST",
            BiosBootImage = @"boot\etfsboot.com",
            BiosLoadSize = 8,
            UefiBootImage = @"efi\microsoft\boot\efisys.bin",
            UefiLoadSize = 1,
        };

        var service = new IsoBuilderService();
        var diagnostics = new List<string>();
        service.Diagnostic += (_, message) => diagnostics.Add(message);

        var request = new IsoBuildRequest
        {
            SourceDirectory = tree,
            OutputIsoPath = output,
            VolumeLabel = "TINYWIN_TEST",
            BootGeometry = geometry,
        };

        await service.BuildAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(output));

        var verification = await service.VerifyAsync(
            output, tree, geometry, TestContext.Current.CancellationToken);

        Assert.True(verification.Passed, verification.Describe());
        Assert.Equal(4, verification.FilesInImage);

        // The name that -untranslated-filenames would have truncated at 37 characters.
        Assert.Equal(ElToritoPlatform.Bios, verification.Catalog?.Bios?.Platform);
        Assert.Equal(8, verification.Catalog?.Bios?.LoadSize);
        Assert.Equal(ElToritoPlatform.Uefi, verification.Catalog?.Uefi?.Platform);

        // xorriso forces the full EFI image size; the build says so rather than hiding it.
        Assert.Contains(diagnostics, d => d.Contains("UEFI boot load size", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_over_long_name_stops_the_build_before_anything_is_written()
    {
        await SkipIfNoXorrisoAsync();

        using var work = TestFiles.NewTempDirectory();
        var tree = Path.Combine(work.Path, "iso");
        var output = Path.Combine(work.Path, "tinywin.iso");

        WriteBootImage(Path.Combine(tree, "boot", "etfsboot.com"), 4096);
        WriteBootImage(Path.Combine(tree, "efi", "microsoft", "boot", "efisys.bin"), 2048);
        WriteText(Path.Combine(tree, "sources", new string('x', 104) + ".dll"), "too long for Joliet");

        var request = new IsoBuildRequest
        {
            SourceDirectory = tree,
            OutputIsoPath = output,
            BootGeometry = new IsoBootGeometry
            {
                VolumeId = "TINYWIN_TEST",
                BiosBootImage = @"boot\etfsboot.com",
                BiosLoadSize = 8,
                UefiBootImage = @"efi\microsoft\boot\efisys.bin",
                UefiLoadSize = 4,
            },
        };

        var exception = await Assert.ThrowsAsync<IsoBuilderException>(
            () => new IsoBuilderService().BuildAsync(
                request, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Joliet", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output), "No media should be written when the preflight fails.");
    }

    [Fact]
    public async Task Building_without_boot_geometry_uses_the_observed_defaults_and_says_so()
    {
        await SkipIfNoXorrisoAsync();

        using var work = TestFiles.NewTempDirectory();
        var tree = Path.Combine(work.Path, "iso");
        var output = Path.Combine(work.Path, "tinywin.iso");

        WriteBootImage(Path.Combine(tree, "boot", "etfsboot.com"), 4096);
        WriteBootImage(Path.Combine(tree, "efi", "microsoft", "boot", "efisys.bin"), 1_474_560);
        WriteText(Path.Combine(tree, "setup.exe"), "not really setup");

        var service = new IsoBuilderService();
        var diagnostics = new List<string>();
        service.Diagnostic += (_, message) => diagnostics.Add(message);

        var request = new IsoBuildRequest
        {
            SourceDirectory = tree,
            OutputIsoPath = output,
            VolumeLabel = "TINYWIN_TEST",
        };

        await service.BuildAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(output));

        // The fallback is allowed, but never quiet: an assumed load size that goes unmentioned is
        // the silent no-op CLAUDE.md forbids.
        Assert.Contains(
            diagnostics,
            d => d.Contains("BootGeometry was null", StringComparison.Ordinal) &&
                 d.Contains(nameof(IsoBuilderService.ReadBootGeometryAsync), StringComparison.Ordinal));

        var written = await service.VerifyAsync(
            output,
            tree,
            IsoDefaults.WindowsMediaGeometry("TINYWIN_TEST"),
            TestContext.Current.CancellationToken);

        Assert.Equal(IsoDefaults.BiosLoadSize, written.Catalog?.Bios?.LoadSize);
    }

    [Fact]
    public void The_default_geometry_is_what_real_media_declares()
    {
        // Captured from Windows 11 25H2 (26200) with -report_el_torito plain. If these ever change,
        // the media changed - do not adjust them to make a build pass.
        var geometry = IsoDefaults.WindowsMediaGeometry();

        Assert.Equal("CCCOMA_X64FRE_EN-US_DV9", geometry.VolumeId);
        Assert.Equal(8, geometry.BiosLoadSize);
        Assert.Equal(1, geometry.UefiLoadSize);
        Assert.Equal(@"boot\etfsboot.com", geometry.BiosBootImage);
        Assert.Equal(@"efi\microsoft\boot\efisys.bin", geometry.UefiBootImage);
    }

    private static async Task SkipIfNoXorrisoAsync()
    {
        var backends = await new IsoBuilderService().ProbeBackendsAsync(TestContext.Current.CancellationToken);

        if (!backends.Single(b => b.Kind == IsoBackendKind.Xorriso).Available)
        {
            Assert.Skip("xorriso is not vendored here; run tools/fetch-xorriso.ps1.");
        }
    }

    private static void WriteBootImage(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var bytes = new byte[length];
        bytes[510] = 0x55;
        bytes[511] = 0xAA;
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
