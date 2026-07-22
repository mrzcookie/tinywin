namespace TinyWin.IsoBuilder.Tests;

public sealed class CygwinPathTests
{
    /// <summary>
    /// The golden test the spike asked for. Every row in Golden/cygwin-paths.txt is a shape TinyWin
    /// hands to xorriso; getting any of them wrong produces a failure that names a path nobody wrote.
    /// </summary>
    [Fact]
    public void Golden_table_of_windows_paths_converts_exactly()
    {
        var rows = TestFiles.ReadGoldenLines("cygwin-paths.txt");
        Assert.NotEmpty(rows);

        var failures = new List<string>();

        foreach (var row in rows)
        {
            var separator = row.IndexOf(" => ", StringComparison.Ordinal);
            Assert.True(separator > 0, $"Malformed golden row: '{row}'");

            var input = row[..separator];
            var expected = row[(separator + 4)..];
            var actual = CygwinPath.FromWindows(input);

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                failures.Add($"'{input}' => '{actual}', expected '{expected}'");
            }
        }

        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(@"scratch\tree")]
    [InlineData(@"..\tree")]
    [InlineData("tree")]
    public void Relative_paths_are_rejected(string path)
    {
        // xorriso would resolve these against its own working directory and produce nonsense.
        Assert.Throws<ArgumentException>(() => CygwinPath.FromWindows(path));
    }

    [Theory]
    [InlineData("C:")]
    [InlineData(@"C:tree")]
    public void Drive_relative_paths_are_rejected(string path) =>
        Assert.Throws<ArgumentException>(() => CygwinPath.FromWindows(path));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_paths_are_rejected(string path) =>
        Assert.Throws<ArgumentException>(() => CygwinPath.FromWindows(path));

    [Fact]
    public void Conversion_is_idempotent()
    {
        var once = CygwinPath.FromWindows(@"C:\scratch\iso");
        Assert.Equal(once, CygwinPath.FromWindows(once));
    }

    [Theory]
    [InlineData(@"boot\etfsboot.com", "boot/etfsboot.com")]
    [InlineData("boot/etfsboot.com", "boot/etfsboot.com")]
    [InlineData(@"\efi\microsoft\boot\efisys.bin", "efi/microsoft/boot/efisys.bin")]
    [InlineData("/efi/microsoft/boot/efisys.bin", "efi/microsoft/boot/efisys.bin")]
    public void Iso_relative_paths_stay_relative(string input, string expected)
    {
        // -b and -e name files *inside* the image, so they must not gain a /cygdrive prefix.
        Assert.Equal(expected, CygwinPath.ToIsoRelative(input));
    }
}
