using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder.Tests;

public sealed class XorrisoCommandLineTests
{
    private static readonly IsoBootGeometry RealMediaGeometry = new()
    {
        // Read from real Windows 11 25H2 (26200) media with -report_el_torito plain.
        VolumeId = "CCCOMA_X64FRE_EN-US_DV9",
        BiosBootImage = @"boot\etfsboot.com",
        BiosLoadSize = 8,
        UefiBootImage = @"efi\microsoft\boot\efisys.bin",
        UefiLoadSize = 1,
    };

    private static readonly IsoBuildRequest RealMediaRequest = new()
    {
        SourceDirectory = @"C:\scratch\iso",
        OutputIsoPath = @"D:\out\tinywin.iso",
        VolumeLabel = "CCCOMA_X64FRE_EN-US_DV9",
        BootGeometry = RealMediaGeometry,
    };

    [Fact]
    public void Build_arguments_match_the_golden_command_line()
    {
        var expected = TestFiles.ReadGoldenLines("xorriso-build.args");
        var actual = XorrisoCommandLine.BuildArguments(RealMediaRequest, RealMediaGeometry);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Build_arguments_never_include_boot_info_table()
    {
        // -boot-info-table rewrites bytes inside the boot image. Applying it to Microsoft's
        // etfsboot.com would corrupt a file we must pass through untouched.
        var arguments = XorrisoCommandLine.BuildArguments(RealMediaRequest, RealMediaGeometry);

        Assert.DoesNotContain("-boot-info-table", arguments);
    }

    [Fact]
    public void Build_arguments_never_include_isohybrid_options()
    {
        // A no-op without a SYSLINUX isohdpfx.bin, and not oscdimg parity. Spike section 7.
        var arguments = XorrisoCommandLine.BuildArguments(RealMediaRequest, RealMediaGeometry);

        Assert.DoesNotContain(arguments, a => a.StartsWith("-isohybrid", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_arguments_never_ask_for_udf()
    {
        // xorriso has no UDF support at all; asking exits 32 with "Unsupported option".
        var arguments = XorrisoCommandLine.BuildArguments(RealMediaRequest, RealMediaGeometry);

        Assert.DoesNotContain("-udf", arguments);
        Assert.DoesNotContain("-udfver102", arguments);
    }

    [Fact]
    public void Build_arguments_use_the_only_name_preserving_option_set()
    {
        var arguments = XorrisoCommandLine.BuildArguments(RealMediaRequest, RealMediaGeometry);

        Assert.Contains("-J", arguments);
        Assert.Contains("-joliet-long", arguments);

        // Both of these silently truncate long names - at 31 and 37 characters respectively.
        Assert.DoesNotContain("-full-iso9660-filenames", arguments);
        Assert.DoesNotContain("-untranslated-filenames", arguments);
    }

    [Fact]
    public void Build_arguments_request_iso_level_3_for_multi_extent()
    {
        var arguments = XorrisoCommandLine.BuildArguments(RealMediaRequest, RealMediaGeometry);
        var index = arguments.ToList().IndexOf("-iso-level");

        Assert.True(index >= 0, "-iso-level is what lifts the 4 GiB single-extent ceiling.");
        Assert.Equal("3", arguments[index + 1]);
    }

    [Fact]
    public void Both_boot_load_sizes_come_from_the_geometry()
    {
        var geometry = RealMediaGeometry with { BiosLoadSize = 12, UefiLoadSize = 2848 };
        var arguments = XorrisoCommandLine.BuildArguments(RealMediaRequest, geometry).ToList();

        var loadSizes = arguments
            .Select((value, index) => (value, index))
            .Where(pair => pair.value == "-boot-load-size")
            .Select(pair => arguments[pair.index + 1])
            .ToList();

        Assert.Equal(["12", "2848"], loadSizes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(8, 0)]
    [InlineData(-1, 1)]
    public void A_missing_load_size_is_refused_rather_than_guessed(int bios, int uefi)
    {
        var geometry = RealMediaGeometry with { BiosLoadSize = bios, UefiLoadSize = uefi };

        Assert.Throws<IsoBuilderException>(
            () => XorrisoCommandLine.BuildArguments(RealMediaRequest, geometry));
    }

    [Fact]
    public void Output_precedes_the_source_tree()
    {
        // The reverse of oscdimg's order. Getting this backwards silently writes the ISO into the
        // staging directory's name.
        var arguments = XorrisoCommandLine.BuildArguments(RealMediaRequest, RealMediaGeometry).ToList();

        var output = arguments.IndexOf("-o");
        Assert.Equal("/cygdrive/d/out/tinywin.iso", arguments[output + 1]);
        Assert.Equal("/cygdrive/c/scratch/iso", arguments[output + 2]);
        Assert.Equal(arguments.Count - 1, output + 2);
    }

    [Fact]
    public void Report_arguments_convert_the_iso_path()
    {
        var plain = XorrisoCommandLine.ReportElToritoArguments(
            @"C:\Users\Zachary\Downloads\win11.iso", ElToritoReportFormat.Plain);

        Assert.Contains("/cygdrive/c/Users/Zachary/Downloads/win11.iso", plain);
        Assert.Contains("plain", plain);

        var asMkisofs = XorrisoCommandLine.ReportElToritoArguments(
            @"C:\Users\Zachary\Downloads\win11.iso", ElToritoReportFormat.AsMkisofs);

        Assert.Contains("as_mkisofs", asMkisofs);
    }

    [Fact]
    public void Every_invocation_raises_the_exit_code_threshold()
    {
        // Real Microsoft media emits two SORRY events because its boot images are hidden. Without
        // -return_with FAILURE 32, simply reading a good 25H2 ISO exits 32.
        foreach (var arguments in new[]
                 {
                     XorrisoCommandLine.BuildArguments(RealMediaRequest, RealMediaGeometry),
                     XorrisoCommandLine.ReportElToritoArguments("C:\\a.iso", ElToritoReportFormat.Plain),
                     XorrisoCommandLine.ListAllFilesArguments("C:\\a.iso"),
                     XorrisoCommandLine.ListDirectoryArguments("C:\\a.iso", "/sources"),
                 })
        {
            Assert.Equal("-return_with", arguments[0]);
            Assert.Equal("FAILURE", arguments[1]);
            Assert.Equal("32", arguments[2]);
        }
    }
}
