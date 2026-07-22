using TinyWin.Unattend;

namespace TinyWin.Tests.Unattend;

/// <summary>
/// Golden-file suite. See <see cref="GoldenXml"/> for how to regenerate the expected files.
/// </summary>
public sealed class UnattendGoldenTests
{
    public static TheoryData<string> Cases => UnattendCases.Names;

    [Theory]
    [MemberData(nameof(Cases))]
    public void Generated_xml_matches_the_golden_file(string caseName)
    {
        var testCase = UnattendCases.Get(caseName);

        var xml = new UnattendGenerator().Generate(testCase.Options, testCase.Architecture);

        GoldenXml.Verify(caseName, xml);
    }

    /// <summary>
    /// Catches golden files left behind by a renamed or deleted case. Without this a stale file
    /// sits in the tree forever, looking like coverage it no longer provides.
    /// </summary>
    [Fact]
    public void Every_golden_file_belongs_to_a_case()
    {
        var expected = UnattendCases.All.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        var orphans = Directory.EnumerateFiles(GoldenXml.DirectoryPath, "*.xml")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => !expected.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            orphans.Count == 0,
            $"Golden files with no matching case: {string.Join(", ", orphans)}. Delete them or restore the case.");
    }
}
