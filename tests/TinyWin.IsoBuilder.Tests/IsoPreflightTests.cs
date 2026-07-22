namespace TinyWin.IsoBuilder.Tests;

public sealed class IsoPreflightTests
{
    [Fact]
    public void A_basename_at_the_limit_is_accepted()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile(Path.Combine("sources", new string('a', 99) + ".dll"), 16);

        var result = IsoPreflight.Inspect(tree.Path);

        Assert.True(result.CanBuild);
        Assert.Empty(result.NameViolations);
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void A_basename_one_over_the_limit_fails_loudly()
    {
        using var tree = TestFiles.NewTempDirectory();
        var name = new string('a', 100) + ".dll";
        Assert.Equal(104, name.Length);
        tree.WriteFile(Path.Combine("sources", name), 16);

        var result = IsoPreflight.Inspect(tree.Path);

        Assert.False(result.CanBuild);
        var violation = Assert.Single(result.NameViolations);
        Assert.Equal(name, violation.Name);
        Assert.Equal(104, violation.NameLength);
        Assert.False(violation.IsDirectory);

        // Emitting silently truncated media is the failure this exists to prevent.
        var exception = Assert.Throws<IsoBuilderException>(() => IsoPreflight.ThrowIfUnbuildable(result));
        Assert.Contains(name, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Over_long_directory_names_are_caught_too()
    {
        using var tree = TestFiles.NewTempDirectory();
        var directory = new string('d', 120);
        tree.WriteFile(Path.Combine(directory, "small.txt"), 1);

        var result = IsoPreflight.Inspect(tree.Path);

        var violation = Assert.Single(result.NameViolations);
        Assert.True(violation.IsDirectory);
        Assert.Equal(directory, violation.Name);
    }

    [Fact]
    public void A_long_volume_label_warns_but_does_not_block()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile("setup.exe", 8);

        // Microsoft's own label is 23 characters. Refusing it would make stock media unbuildable.
        var result = IsoPreflight.Inspect(tree.Path, "CCCOMA_X64FRE_EN-US_DV9");

        Assert.True(result.CanBuild);
        Assert.Contains(result.Warnings, w => w.Contains("CCCOMA_X64FRE_EN", StringComparison.Ordinal));
    }

    [Fact]
    public void A_short_volume_label_produces_no_warning()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile("setup.exe", 8);

        var result = IsoPreflight.Inspect(tree.Path, "TINYWIN");

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void An_empty_tree_is_reported_rather_than_built_silently()
    {
        using var tree = TestFiles.NewTempDirectory();

        var result = IsoPreflight.Inspect(tree.Path);

        Assert.Equal(0, result.FileCount);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void A_file_over_four_gibibytes_flags_the_multi_extent_dependency()
    {
        using var tree = TestFiles.NewTempDirectory();

        // Sparse: SetLength does not write 4 GiB to disk.
        tree.WriteFile(Path.Combine("sources", "install.wim"), 4L * 1024 * 1024 * 1024 + 1);

        var result = IsoPreflight.Inspect(tree.Path);

        Assert.True(result.RequiresMultiExtent);
        Assert.Equal(Path.Combine("sources", "install.wim"), result.LargestFilePath);
    }

    [Fact]
    public void A_file_at_the_single_extent_ceiling_does_not_need_multi_extent()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile(Path.Combine("sources", "install.wim"), uint.MaxValue);

        var result = IsoPreflight.Inspect(tree.Path);

        Assert.False(result.RequiresMultiExtent);
    }

    [Fact]
    public void Counts_and_sizes_are_reported()
    {
        using var tree = TestFiles.NewTempDirectory();
        tree.WriteFile(Path.Combine("boot", "etfsboot.com"), 4096);
        tree.WriteFile(Path.Combine("sources", "boot.wim"), 1024);
        tree.WriteFile("setup.exe", 512);

        var result = IsoPreflight.Inspect(tree.Path);

        Assert.Equal(3, result.FileCount);
        Assert.Equal(2, result.DirectoryCount);
        Assert.Equal(4096 + 1024 + 512, result.TotalBytes);
        Assert.Equal(4096, result.LargestFileBytes);
    }

    [Fact]
    public void A_missing_tree_is_an_error_not_an_empty_result() =>
        Assert.Throws<DirectoryNotFoundException>(
            () => IsoPreflight.Inspect(Path.Combine(Path.GetTempPath(), "tinywin-does-not-exist")));
}
