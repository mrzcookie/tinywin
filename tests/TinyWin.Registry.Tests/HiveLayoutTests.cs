using TinyWin.Catalog.Models;

namespace TinyWin.Registry.Tests;

public class HiveLayoutTests
{
    [Theory]
    [InlineData(RegistryHive.Components, @"Windows\System32\config\COMPONENTS")]
    [InlineData(RegistryHive.Default, @"Windows\System32\config\default")]
    [InlineData(RegistryHive.NtUser, @"Users\Default\ntuser.dat")]
    [InlineData(RegistryHive.Software, @"Windows\System32\config\SOFTWARE")]
    [InlineData(RegistryHive.System, @"Windows\System32\config\SYSTEM")]
    public void Each_hive_maps_to_its_file_in_the_image(RegistryHive hive, string expected) =>
        Assert.Equal(expected, HiveLayout.RelativeFilePath(hive));

    [Fact]
    public void File_path_is_rooted_at_the_mount_point() =>
        Assert.Equal(
            @"C:\scratch\mount\Windows\System32\config\SOFTWARE",
            HiveLayout.FilePath(@"C:\scratch\mount", RegistryHive.Software));

    [Fact]
    public void File_path_rejects_a_blank_mount_path() =>
        Assert.Throws<ArgumentException>(() => HiveLayout.FilePath("  ", RegistryHive.Software));

    [Fact]
    public void Every_hive_has_a_distinct_mount_name()
    {
        var names = HiveLayout.All.Select(HiveLayout.MountName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Mount_names_are_z_prefixed_so_they_are_obvious_in_regedit() =>
        Assert.All(HiveLayout.All, hive => Assert.StartsWith("z", HiveLayout.MountName(hive), StringComparison.Ordinal));

    [Theory]
    [InlineData("zTW-SOFTWARE", true)]
    [InlineData("ztw-software", true)]
    [InlineData("zSOFTWARE", false)]
    [InlineData("SOFTWARE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recovery_only_recognises_our_own_mount_points(string? name, bool expected) =>
        Assert.Equal(expected, HiveLayout.IsTinyWinMountName(name));

    [Fact]
    public void All_lists_every_hive_in_the_catalog_enum() =>
        Assert.Equal(Enum.GetValues<RegistryHive>().Order(), HiveLayout.All.Order());
}
