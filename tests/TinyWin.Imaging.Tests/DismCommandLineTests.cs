using TinyWin.Core.Abstractions;
using TinyWin.Imaging;
using TinyWin.Imaging.Dism;

namespace TinyWin.Imaging.Tests;

/// <summary>
/// Golden tests for every command line the backend can build.
/// </summary>
/// <remarks>
/// Written as literal expected strings rather than assembled from the same helpers the production
/// code uses. A test that rebuilds the string the same way only proves the builder is deterministic.
/// </remarks>
public class DismCommandLineTests
{
    private const string Wim = @"D:\sources\install.wim";
    private const string Mount = @"C:\TinyWin\mount";

    [Fact]
    public void Get_wim_info_lists_indexes()
    {
        Assert.Equal(
            @"/English /Get-WimInfo /WimFile:""D:\sources\install.wim""",
            DismCommandLine.GetWimInfo(Wim));
    }

    [Fact]
    public void Get_wim_info_for_one_index_adds_the_index()
    {
        Assert.Equal(
            @"/English /Get-WimInfo /WimFile:""D:\sources\install.wim"" /Index:6",
            DismCommandLine.GetWimInfo(Wim, 6));
    }

    [Fact]
    public void Mount_wim_names_file_index_and_directory()
    {
        Assert.Equal(
            @"/English /Mount-Wim /WimFile:""D:\sources\install.wim"" /Index:6 /MountDir:""C:\TinyWin\mount""",
            DismCommandLine.MountWim(Wim, 6, Mount));
    }

    [Fact]
    public void Unmount_commits_when_asked()
    {
        Assert.Equal(
            @"/English /Unmount-Wim /MountDir:""C:\TinyWin\mount"" /Commit",
            DismCommandLine.UnmountWim(Mount, commit: true));
    }

    /// <summary>The cancellation unwind path. Getting this wrong writes a half-built image.</summary>
    [Fact]
    public void Unmount_discards_when_not_committing()
    {
        Assert.Equal(
            @"/English /Unmount-Wim /MountDir:""C:\TinyWin\mount"" /Discard",
            DismCommandLine.UnmountWim(Mount, commit: false));
    }

    [Fact]
    public void Get_mounted_wim_info_takes_no_target()
    {
        Assert.Equal("/English /Get-MountedWimInfo", DismCommandLine.GetMountedWimInfo());
    }

    [Fact]
    public void Cleanup_mountpoints_takes_no_target()
    {
        Assert.Equal("/English /Cleanup-Mountpoints", DismCommandLine.CleanupMountpoints());
    }

    [Fact]
    public void Get_provisioned_appx_scopes_to_the_mounted_image()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Get-ProvisionedAppxPackages",
            DismCommandLine.GetProvisionedAppxPackages(Mount));
    }

    [Fact]
    public void Remove_provisioned_appx_passes_the_full_package_name()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Remove-ProvisionedAppxPackage " +
            @"/PackageName:""Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe""",
            DismCommandLine.RemoveProvisionedAppxPackage(
                Mount, "Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"));
    }

    [Fact]
    public void Get_capabilities_scopes_to_the_mounted_image()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Get-Capabilities",
            DismCommandLine.GetCapabilities(Mount));
    }

    [Fact]
    public void Remove_capability_names_the_capability()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Remove-Capability " +
            @"/CapabilityName:""Browser.InternetExplorer~~~~0.0.11.0""",
            DismCommandLine.RemoveCapability(Mount, "Browser.InternetExplorer~~~~0.0.11.0"));
    }

    [Fact]
    public void Get_features_scopes_to_the_mounted_image()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Get-Features",
            DismCommandLine.GetFeatures(Mount));
    }

    [Fact]
    public void Disable_feature_without_payload_removal_omits_remove()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Disable-Feature /FeatureName:""WorkFolders-Client""",
            DismCommandLine.DisableFeature(Mount, "WorkFolders-Client", removePayload: false));
    }

    [Fact]
    public void Disable_feature_with_payload_removal_appends_remove()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Disable-Feature /FeatureName:""WorkFolders-Client"" /Remove",
            DismCommandLine.DisableFeature(Mount, "WorkFolders-Client", removePayload: true));
    }

    [Fact]
    public void Get_packages_scopes_to_the_mounted_image()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Get-Packages",
            DismCommandLine.GetPackages(Mount));
    }

    [Fact]
    public void Remove_package_names_the_identity()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Remove-Package " +
            @"/PackageName:""Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~amd64~~10.0.26200.8037""",
            DismCommandLine.RemovePackage(
                Mount, "Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~amd64~~10.0.26200.8037"));
    }

    [Fact]
    public void Cleanup_image_without_reset_base_stops_at_component_cleanup()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Cleanup-Image /StartComponentCleanup",
            DismCommandLine.CleanupImage(Mount, resetBase: false));
    }

    [Fact]
    public void Cleanup_image_with_reset_base_appends_reset_base()
    {
        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Cleanup-Image /StartComponentCleanup /ResetBase",
            DismCommandLine.CleanupImage(Mount, resetBase: true));
    }

    [Fact]
    public void Export_image_names_source_index_destination_and_compression()
    {
        Assert.Equal(
            @"/English /Export-Image /SourceImageFile:""C:\TinyWin\work\install.wim"" /SourceIndex:1 " +
            @"/DestinationImageFile:""C:\TinyWin\out\install.wim"" /Compress:recovery",
            DismCommandLine.ExportImage(
                @"C:\TinyWin\work\install.wim", 1, @"C:\TinyWin\out\install.wim", CompressionType.Recovery));
    }

    [Theory]
    [InlineData(CompressionType.None, "none")]
    [InlineData(CompressionType.Fast, "fast")]
    [InlineData(CompressionType.Maximum, "max")]
    [InlineData(CompressionType.Recovery, "recovery")]
    public void Compression_maps_to_dism_spelling(CompressionType compression, string expected)
    {
        Assert.Equal(expected, DismCommandLine.CompressionArgument(compression));
    }

    /// <summary>
    /// The single most important property in this file. A parser keyed on English output that runs
    /// against a localised DISM finds nothing and reports every action as a no-op — see
    /// docs/spikes/dism-backend.md §5.
    /// </summary>
    [Fact]
    public void Every_command_starts_with_English()
    {
        foreach (var command in AllCommands())
        {
            Assert.StartsWith("/English", command, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Paths_are_quoted_so_spaces_survive()
    {
        var command = DismCommandLine.MountWim(@"C:\My Images\install.wim", 1, @"C:\Program Files\mount");

        Assert.Contains(@"/WimFile:""C:\My Images\install.wim""", command, StringComparison.Ordinal);
        Assert.Contains(@"/MountDir:""C:\Program Files\mount""", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Scratch_directory_is_added_to_image_servicing_commands()
    {
        var options = new DismOptions { ScratchDirectory = @"E:\scratch" };

        Assert.Equal(
            @"/English /Image:""C:\TinyWin\mount"" /Cleanup-Image /StartComponentCleanup /ResetBase " +
            @"/ScratchDir:""E:\scratch""",
            DismCommandLine.CleanupImage(Mount, resetBase: true, options));
    }

    /// <summary>
    /// /ScratchDir is not a universal global option. Appending it to /Get-WimInfo turns a working
    /// command into an argument error, which is why it is gated on the image-servicing forms.
    /// </summary>
    [Fact]
    public void Scratch_directory_is_not_added_to_non_servicing_commands()
    {
        var options = new DismOptions { ScratchDirectory = @"E:\scratch" };

        Assert.DoesNotContain("/ScratchDir", DismCommandLine.GetWimInfo(Wim, options), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/ScratchDir",
            DismCommandLine.ExportImage(Wim, 1, @"C:\out.wim", CompressionType.Maximum, options),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Log_options_apply_to_every_command()
    {
        var options = new DismOptions
        {
            LogPath = @"C:\TinyWin\logs\dism.log",
            LogLevel = DismLogLevel.ErrorsWarningsInfo,
        };

        Assert.Equal(
            @"/English /Get-MountedWimInfo /LogPath:""C:\TinyWin\logs\dism.log"" /LogLevel:3",
            DismCommandLine.GetMountedWimInfo(options));
    }

    [Fact]
    public void Default_options_add_nothing()
    {
        Assert.Equal(DismCommandLine.GetWimInfo(Wim), DismCommandLine.GetWimInfo(Wim, DismOptions.Default));
    }

    /// <summary>
    /// Catalog data is the source of these values. A quote inside one would end the quoted argument
    /// early and let the rest be read as switches, so it is refused rather than escaped — Windows
    /// offers no escaping DISM would honour.
    /// </summary>
    [Fact]
    public void A_quote_in_a_value_is_refused_rather_than_mangled()
    {
        Assert.Throws<ArgumentException>(
            () => DismCommandLine.RemoveCapability(Mount, @"Evil"" /Remove-Package /PackageName:Foo"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_values_are_refused(string value)
    {
        Assert.Throws<ArgumentException>(() => DismCommandLine.RemoveProvisionedAppxPackage(Mount, value));
    }

    private static IEnumerable<string> AllCommands() =>
    [
        DismCommandLine.GetWimInfo(Wim),
        DismCommandLine.GetWimInfo(Wim, 1),
        DismCommandLine.MountWim(Wim, 1, Mount),
        DismCommandLine.UnmountWim(Mount, commit: true),
        DismCommandLine.UnmountWim(Mount, commit: false),
        DismCommandLine.GetMountedWimInfo(),
        DismCommandLine.CleanupMountpoints(),
        DismCommandLine.GetProvisionedAppxPackages(Mount),
        DismCommandLine.RemoveProvisionedAppxPackage(Mount, "Microsoft.BingNews_1.0_neutral_~_8wekyb3d8bbwe"),
        DismCommandLine.GetCapabilities(Mount),
        DismCommandLine.RemoveCapability(Mount, "Browser.InternetExplorer~~~~0.0.11.0"),
        DismCommandLine.GetFeatures(Mount),
        DismCommandLine.DisableFeature(Mount, "WorkFolders-Client", removePayload: true),
        DismCommandLine.GetPackages(Mount),
        DismCommandLine.RemovePackage(Mount, "Some-Package~31bf3856ad364e35~amd64~~10.0.1.0"),
        DismCommandLine.CleanupImage(Mount, resetBase: true),
        DismCommandLine.ExportImage(Wim, 1, @"C:\out.wim", CompressionType.Recovery),
    ];
}
