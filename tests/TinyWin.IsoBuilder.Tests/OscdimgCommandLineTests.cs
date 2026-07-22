using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder.Tests;

public sealed class OscdimgCommandLineTests
{
    private static readonly IsoBuildRequest Request = new()
    {
        SourceDirectory = @"C:\scratch\iso",
        OutputIsoPath = @"D:\out\tinywin.iso",
        VolumeLabel = "CCCOMA_X64FRE_EN-US_DV9",
    };

    [Fact]
    public void Build_arguments_match_the_golden_command_line()
    {
        var expected = TestFiles.ReadGoldenLines("oscdimg-build.args");
        var actual = OscdimgCommandLine.BuildArguments(Request);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Geometry_supplies_the_boot_images_but_not_load_sizes()
    {
        var geometry = new IsoBootGeometry
        {
            VolumeId = "CCCOMA_X64FRE_EN-US_DV9",
            BiosBootImage = @"boot\custom.com",
            BiosLoadSize = 8,
            UefiBootImage = @"efi\custom.bin",
            UefiLoadSize = 1,
        };

        var arguments = OscdimgCommandLine.BuildArguments(
            OscdimgCommandLine.ApplyGeometry(Request, geometry));

        Assert.Contains(
            @"-bootdata:2#p0,e,bC:\scratch\iso\boot\custom.com#pEF,e,bC:\scratch\iso\efi\custom.bin",
            arguments);

        // oscdimg derives the load sizes from the boot images, so they are never passed on.
        Assert.DoesNotContain(arguments, a => a.Contains("-boot-load-size", StringComparison.Ordinal));
    }

    [Fact]
    public void Paths_stay_native_and_are_never_cygwin_converted()
    {
        var arguments = OscdimgCommandLine.BuildArguments(Request);

        Assert.DoesNotContain(arguments, a => a.Contains("/cygdrive/", StringComparison.Ordinal));
    }

    [Fact]
    public void Udf_is_requested_because_oscdimg_can_write_it()
    {
        // This is the one place UDF appears in TinyWin: it is how Microsoft's own media escapes the
        // 4 GiB single-extent ceiling, and oscdimg is the only backend that can produce it.
        var arguments = OscdimgCommandLine.BuildArguments(Request);

        Assert.Contains("-u2", arguments);
        Assert.Contains("-udfver102", arguments);
    }
}
