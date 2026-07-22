namespace TinyWin.Registry.Tests;

public class RegistryKeyPathTests
{
    [Theory]
    [InlineData(@"Microsoft\Windows\CurrentVersion", @"Microsoft\Windows\CurrentVersion")]
    [InlineData(@"\Microsoft\Windows\", @"Microsoft\Windows")]
    [InlineData(@"Microsoft\\Windows", @"Microsoft\Windows")]
    [InlineData("Microsoft/Windows/Explorer", @"Microsoft\Windows\Explorer")]
    [InlineData("  Microsoft\\Windows  ", @"Microsoft\Windows")]
    [InlineData("Policies", "Policies")]
    public void Normalizes_to_a_canonical_backslash_path(string input, string expected) =>
        Assert.Equal(expected, RegistryKeyPath.Normalize(input));

    [Fact]
    public void Preserves_key_names_that_start_with_a_dot() =>
        Assert.Equal(@"Microsoft\.NETFramework", RegistryKeyPath.Normalize(@"Microsoft\.NETFramework"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\")]
    public void Rejects_an_absent_key(string? input) =>
        Assert.Throws<RegistryActionException>(() => RegistryKeyPath.Normalize(input));

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft")]
    [InlineData(@"hkcu\Software\Microsoft")]
    [InlineData(@"HKEY_CURRENT_USER\Software")]
    public void Rejects_a_pasted_regedit_path_rather_than_silently_stripping_it(string input)
    {
        // Silently repairing this would land the action on a key the author never named, and the
        // no-op report would then be about the wrong key.
        var ex = Assert.Throws<RegistryActionException>(() => RegistryKeyPath.Normalize(input));
        Assert.Contains("relative to the hive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_key_that_includes_our_own_mount_point()
    {
        var ex = Assert.Throws<RegistryActionException>(() => RegistryKeyPath.Normalize(@"zTW-SOFTWARE\Microsoft"));
        Assert.Contains("mount point", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_upward_traversal() =>
        Assert.Throws<RegistryActionException>(() => RegistryKeyPath.Normalize(@"Microsoft\..\..\Other"));

    [Fact]
    public void Rejects_a_path_that_looks_like_a_file_path() =>
        Assert.Throws<RegistryActionException>(() => RegistryKeyPath.Normalize(@"C:\Windows\System32"));

    [Fact]
    public void Under_mount_prefixes_the_loaded_hive() =>
        Assert.Equal(@"zTW-SOFTWARE\Microsoft\Windows", RegistryKeyPath.UnderMount("zTW-SOFTWARE", @"Microsoft\Windows"));

    [Theory]
    [InlineData(@"Microsoft\Windows\Explorer", @"Microsoft\Windows", "Explorer")]
    [InlineData("Policies", null, "Policies")]
    public void Splits_a_leaf_off_the_parent(string input, string? parent, string leaf) =>
        Assert.Equal((parent, leaf), RegistryKeyPath.SplitLeaf(input));
}
