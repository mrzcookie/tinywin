using TinyWin.Imaging.Dism;

namespace TinyWin.Imaging.Tests;

/// <summary>
/// Parser tests against the samples in <c>Samples/</c>.
/// </summary>
/// <remarks>
/// The WimInfo expectations are the real values of the Windows 11 25H2 media on D: — 11 editions,
/// build 26200.8037 — read out of the WIM's own XML header rather than made up. See
/// <c>Samples/README.md</c>.
/// </remarks>
public class DismOutputParserTests
{
    [Fact]
    public void Wim_info_lists_every_edition_on_the_media()
    {
        var indexes = DismOutputParser.ParseWimIndexes(Samples.WimInfoList);

        Assert.Equal(11, indexes.Count);
        Assert.Equal(Enumerable.Range(1, 11), indexes.Select(i => i.Index));
    }

    [Fact]
    public void Wim_info_reads_names_and_sizes()
    {
        var indexes = DismOutputParser.ParseWimIndexes(Samples.WimInfoList);

        var home = indexes[0];
        Assert.Equal("Windows 11 Home", home.Name);
        Assert.Equal("Windows 11 Home", home.Description);
        Assert.Equal(23_831_001_491, home.SizeBytes);

        var pro = indexes.Single(i => i.Index == 6);
        Assert.Equal("Windows 11 Pro", pro.Name);
        Assert.Equal(24_860_631_795, pro.SizeBytes);
    }

    [Fact]
    public void Wim_info_ignores_the_banner_and_the_trailing_prose()
    {
        // "Deployment Image Servicing and Management tool" / "Version: ..." / "Details for image : ..."
        // must not become records, and "The operation completed successfully." must not either.
        var indexes = DismOutputParser.ParseWimIndexes(Samples.WimInfoList);

        Assert.DoesNotContain(indexes, i => i.Index == 0);
        Assert.All(indexes, i => Assert.NotEmpty(i.Name));
    }

    [Fact]
    public void Image_detail_carries_everything_the_edition_model_needs()
    {
        var edition = DismOutputParser.ParseWimImageDetail(Samples.WimInfoIndex6);

        Assert.NotNull(edition);
        Assert.Equal(6, edition.Index);
        Assert.Equal("Windows 11 Pro", edition.Name);
        Assert.Equal("Professional", edition.EditionId);
        Assert.Equal("x64", edition.Architecture);
        Assert.Equal(24_860_631_795, edition.SizeBytes);
        Assert.Equal("en-US", edition.DefaultLanguage);
    }

    /// <summary>
    /// DISM splits the revision into its own field: "Version : 10.0.26200" plus
    /// "ServicePack Build : 8037" is 26200.8037. Losing the split would make every build look like
    /// x.y.z.0, which still classifies correctly but reads wrong in the UI.
    /// </summary>
    [Fact]
    public void Image_detail_recombines_version_and_service_pack_build()
    {
        var edition = DismOutputParser.ParseWimImageDetail(Samples.WimInfoIndex6);

        Assert.NotNull(edition);
        Assert.Equal(new Version(10, 0, 26200, 8037), edition.Version);
        Assert.Equal(26200, edition.Build);
    }

    [Fact]
    public void Image_detail_of_index_one_is_the_home_edition()
    {
        var edition = DismOutputParser.ParseWimImageDetail(Samples.WimInfoIndex1);

        Assert.NotNull(edition);
        Assert.Equal(1, edition.Index);
        Assert.Equal("Core", edition.EditionId);
        Assert.Equal(23_831_001_491, edition.SizeBytes);
    }

    [Fact]
    public void Mounted_images_are_parsed_with_path_and_index()
    {
        var mounted = DismOutputParser.ParseMountedImages(Samples.MountedWimInfo);

        Assert.Equal(2, mounted.Count);
        Assert.Equal(@"C:\TinyWin\mount", mounted[0].MountPath);
        Assert.Equal(@"C:\TinyWin\work\sources\install.wim", mounted[0].WimPath);
        Assert.Equal(6, mounted[0].Index);

        // The second entry has Status "Needs Remount" — a crashed previous run. Preflight has to see
        // it in order to clean it up, so it must not be filtered out here.
        Assert.Equal(@"C:\TinyWin\boot-mount", mounted[1].MountPath);
        Assert.Equal(2, mounted[1].Index);
    }

    [Fact]
    public void Provisioned_appx_is_parsed_with_full_package_names()
    {
        var packages = DismOutputParser.ParseProvisionedAppx(Samples.ProvisionedAppx);

        Assert.Equal(48, packages.Count);

        var news = packages.Single(p => p.DisplayName == "Microsoft.BingNews");
        Assert.Equal("Microsoft.BingNews_4.1.24002.0_neutral_~_8wekyb3d8bbwe", news.PackageName);
        Assert.Equal("4.1.24002.0", news.Version);
    }

    /// <summary>
    /// The line "Getting the list of app packages (.appx or .appxbundle) in this image..." sits
    /// between the banner and the records and is not a field. It must not become one.
    /// </summary>
    [Fact]
    public void The_appx_preamble_is_not_mistaken_for_a_record()
    {
        var packages = DismOutputParser.ParseProvisionedAppx(Samples.ProvisionedAppx);

        Assert.All(packages, p => Assert.DoesNotContain("Getting the list", p.PackageName, StringComparison.Ordinal));
    }

    [Fact]
    public void Capability_states_are_parsed()
    {
        var capabilities = DismOutputParser.ParseCapabilities(Samples.Capabilities);

        Assert.Equal(425, capabilities.Count);
        Assert.Equal(DismComponentState.Installed, capabilities["Media.WindowsMediaPlayer~~~~0.0.12.0"]);
        Assert.Equal(DismComponentState.Absent, capabilities["Print.Fax.Scan~~~~0.0.1.0"]);
    }

    /// <summary>
    /// A capability identity can end in bare tildes with no version at all. Assuming a version
    /// suffix always follows would drop these.
    /// </summary>
    [Fact]
    public void Capability_identities_without_a_version_suffix_are_parsed()
    {
        var capabilities = DismOutputParser.ParseCapabilities(Samples.Capabilities);

        Assert.Equal(DismComponentState.Installed, capabilities["WMIC~~~~"]);
        Assert.Equal(DismComponentState.Absent, capabilities["AzureArcSetup~~~~"]);
    }

    [Fact]
    public void Feature_states_include_payload_removed()
    {
        var features = DismOutputParser.ParseFeatures(Samples.Features);

        Assert.Equal(98, features.Count);
        Assert.Equal(DismComponentState.Enabled, features["WorkFolders-Client"]);
        Assert.Equal(DismComponentState.Disabled, features["Printing-XPSServices-Features"]);

        // NetFx3 is the only payload-removed feature on stock 26200 media.
        Assert.Equal(DismComponentState.DisabledWithPayloadRemoved, features["NetFx3"]);
    }

    [Fact]
    public void Package_states_include_staged()
    {
        var packages = DismOutputParser.ParsePackages(Samples.Packages);

        Assert.Equal(144, packages.Count);
        Assert.Equal(
            DismComponentState.Staged,
            packages["Microsoft-OneCore-ApplicationModel-Sync-Desktop-FOD-Package~31bf3856ad364e35~amd64~~10.0.26100.1742"]);
        Assert.Equal(
            DismComponentState.Installed,
            packages["Microsoft-OneCore-ApplicationModel-Sync-Desktop-FOD-Package~31bf3856ad364e35~amd64~~10.0.26100.8036"]);
    }

    /// <summary>
    /// "Install Time : 3/6/2026 11:54 PM" contains a colon. Splitting on the first ':' rather than
    /// the first " : " would truncate it — and worse, would mis-split any value starting with a time.
    /// </summary>
    [Fact]
    public void Values_containing_colons_survive()
    {
        var records = DismOutputParser.ParseRecords(Samples.Packages, "Package Identity");

        Assert.Equal("3/6/2026 11:54 PM", records[1].Get("Install Time"));
    }

    /// <summary>
    /// Staged packages have "Install Time : " with nothing after it. Present-but-empty must not
    /// read as absent, or a record's fields shift.
    /// </summary>
    [Fact]
    public void An_empty_field_parses_as_empty_not_missing()
    {
        var records = DismOutputParser.ParseRecords(Samples.Packages, "Package Identity");

        Assert.True(records[0].Has("Install Time"));
        Assert.Equal(string.Empty, records[0].Get("Install Time"));
        Assert.Equal("OnDemand Pack", records[0].Get("Release Type"));
    }

    /// <summary>
    /// The Copilot and Outlook entries carry a long semicolon-delimited region list on a single
    /// line. Compared against the raw line rather than a hard-coded length, so the assertion stays
    /// true when the samples are refreshed from newer media.
    /// </summary>
    [Fact]
    public void A_very_long_single_line_value_is_not_truncated()
    {
        var records = DismOutputParser.ParseRecords(Samples.ProvisionedAppx, "PackageName");
        var copilot = records.Single(r => r.Get("DisplayName") == "Microsoft.Copilot");

        var lines = Samples.ProvisionedAppx.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(l => l == "DisplayName : Microsoft.Copilot");
        var expected = lines
            .Skip(start)
            .First(l => l.StartsWith("Regions : ", StringComparison.Ordinal))["Regions : ".Length..];

        Assert.Equal(expected, copilot.Get("Regions"));
        Assert.True(expected.Length > 700, "Sanity: the region list should be hundreds of characters.");
    }

    [Fact]
    public void The_error_740_capture_is_recognised()
    {
        Assert.True(DismOutputParser.TryParseErrorCode(Samples.Error740, out var code));
        Assert.Equal(740, code);
        Assert.False(DismOutputParser.ReportsSuccess(Samples.Error740));
    }

    [Fact]
    public void Hexadecimal_error_codes_are_recognised()
    {
        Assert.True(DismOutputParser.TryParseErrorCode("Error: 0x800f080c", out var code));
        Assert.Equal(unchecked((int)0x800F080C), code);
    }

    [Fact]
    public void Success_prose_is_recognised()
    {
        Assert.True(DismOutputParser.ReportsSuccess(Samples.WimInfoList));
    }

    /// <summary>
    /// Parsing 740 output as a listing must yield nothing rather than throwing. Every enumeration
    /// path hits this on an unelevated machine.
    /// </summary>
    [Fact]
    public void Parsing_an_error_response_yields_no_records_rather_than_throwing()
    {
        Assert.Empty(DismOutputParser.ParseWimIndexes(Samples.Error740));
        Assert.Empty(DismOutputParser.ParseProvisionedAppx(Samples.Error740));
        Assert.Empty(DismOutputParser.ParseCapabilities(Samples.Error740));
        Assert.Empty(DismOutputParser.ParseMountedImages(Samples.Error740));
        Assert.Null(DismOutputParser.ParseWimImageDetail(Samples.Error740));
    }

    [Fact]
    public void Empty_output_yields_no_records()
    {
        Assert.Empty(DismOutputParser.ParseWimIndexes(string.Empty));
        Assert.Empty(DismOutputParser.ParseRecords(string.Empty, "Index"));
    }

    /// <summary>Output arriving with Unix line endings must parse the same way.</summary>
    [Fact]
    public void Line_endings_do_not_change_the_result()
    {
        var crlf = Samples.Capabilities.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        var lf = Samples.Capabilities.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(
            DismOutputParser.ParseCapabilities(crlf).Count,
            DismOutputParser.ParseCapabilities(lf).Count);
    }

    [Theory]
    [InlineData("Installed", DismComponentState.Installed)]
    [InlineData("Enabled", DismComponentState.Enabled)]
    [InlineData("Disabled", DismComponentState.Disabled)]
    [InlineData("Disabled with Payload Removed", DismComponentState.DisabledWithPayloadRemoved)]
    [InlineData("Not Present", DismComponentState.Absent)]
    [InlineData("Superseded", DismComponentState.Superseded)]
    [InlineData("Something New", DismComponentState.Other)]
    public void States_map_to_the_enum(string text, DismComponentState expected)
    {
        Assert.Equal(expected, DismOutputParser.ParseState(text));
    }
}
