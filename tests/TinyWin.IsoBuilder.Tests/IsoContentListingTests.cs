namespace TinyWin.IsoBuilder.Tests;

public sealed class IsoContentListingTests
{
    [Fact]
    public void Listing_yields_relative_paths_and_lengths()
    {
        var entries = IsoContentListing.Parse(TestFiles.ReadFixture("rebuilt-file-listing.txt"));

        Assert.Equal(5, entries.Count);
        Assert.Contains(entries, e => e.Path == @"boot\etfsboot.com" && e.Length == 4096);
        Assert.Contains(entries, e => e.Path == @"efi\microsoft\boot\efisys.bin" && e.Length == 1474560);
        Assert.Contains(entries, e => e.Path == "setup.exe" && e.Length == 99896);
    }

    [Fact]
    public void A_file_past_four_gibibytes_keeps_its_full_length()
    {
        // The whole reason this parser exists: a short install.wim means the multi-extent write
        // went wrong, and that must be caught before the user boots the media.
        var entries = IsoContentListing.Parse(TestFiles.ReadFixture("rebuilt-file-listing.txt"));

        var wim = Assert.Single(entries, e => e.Path == @"sources\install.wim");
        Assert.Equal(7578075168, wim.Length);
        Assert.True(wim.Length > uint.MaxValue);
    }

    [Fact]
    public void Directories_and_banner_lines_are_ignored()
    {
        var entries = IsoContentListing.Parse(TestFiles.ReadFixture("rebuilt-file-listing.txt"));

        Assert.DoesNotContain(entries, e => e.Path == "sources");
        Assert.DoesNotContain(entries, e => e.Path.Contains("xorriso", StringComparison.Ordinal));
    }

    [Fact]
    public void Names_with_spaces_survive()
    {
        const string Line =
            "-rw-r--r--    1 197609   197121       1234 Mar  6 19:03 '/sources/en-us/some file.txt'";

        var entry = Assert.Single(IsoContentListing.Parse(Line));

        Assert.Equal(@"sources\en-us\some file.txt", entry.Path);
        Assert.Equal(1234, entry.Length);
    }
}
