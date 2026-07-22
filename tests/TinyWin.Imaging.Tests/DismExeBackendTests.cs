using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Imaging;
using TinyWin.Imaging.Execution;
using TinyWin.Imaging.Tests.Fakes;

namespace TinyWin.Imaging.Tests;

public class DismExeBackendTests
{
    private static readonly MountedImage Image =
        new(@"C:\TinyWin\work\sources\install.wim", 6, @"C:\TinyWin\mount");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeProcessRunner FullyStockedRunner() =>
        new FakeProcessRunner()
            .Respond("/Get-ProvisionedAppxPackages", Samples.ProvisionedAppx)
            .Respond("/Get-Capabilities", Samples.Capabilities)
            .Respond("/Get-Features", Samples.Features)
            .Respond("/Get-Packages", Samples.Packages)
            .Respond("/Get-MountedWimInfo", Samples.MountedWimInfo);

    private static DismExeBackend NewBackend(FakeProcessRunner runner) => new(runner, DismOptions.Default);

    // ---------------------------------------------------------------- enumeration

    [Fact]
    public async Task Get_editions_lists_then_details_each_index()
    {
        var runner = new FakeProcessRunner()
            .Respond("/Index:6", Samples.WimInfoIndex6)
            .Respond("/Index:1", Samples.WimInfoIndex1)
            .Respond("/Get-WimInfo", Samples.WimInfoList);
        using var backend = NewBackend(runner);

        var editions = await backend.GetEditionsAsync(@"D:\sources\install.wim", Ct);

        Assert.Equal(11, editions.Count);
        // One listing call, plus one detail call per index — the listing form does not report
        // architecture, version or edition id.
        Assert.Equal(12, runner.CountMatching("/Get-WimInfo"));
        Assert.Equal("Professional", editions.Single(e => e.Index == 6).EditionId);
    }

    [Fact]
    public async Task Get_mounted_images_finds_strays_from_a_crashed_run()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        var mounted = await backend.GetMountedImagesAsync(Ct);

        Assert.Equal(2, mounted.Count);
        Assert.Contains("/Get-MountedWimInfo", runner.CommandLines[0], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- NoTarget

    /// <summary>
    /// The packages tiny11builder removes that no longer ship on 26100/26200. Every one must report
    /// <see cref="ActionStatus.NoTarget"/> — this path firing is the design working, not a bug, and
    /// on real media it fires a lot.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.XboxApp")]
    [InlineData("Microsoft.WindowsMaps")]
    [InlineData("Microsoft.People")]
    [InlineData("microsoft.windowscommunicationsapps")]
    [InlineData("Microsoft.ZuneVideo")]
    [InlineData("Microsoft.Print3D")]
    [InlineData("Microsoft.3DBuilder")]
    [InlineData("Microsoft.MixedReality.Portal")]
    [InlineData("Microsoft.549981C3F5F10")]
    [InlineData("Microsoft.Getstarted")]
    [InlineData("Microsoft.SkypeApp")]
    [InlineData("MicrosoftTeams")]
    public async Task Appx_that_no_longer_ships_reports_NoTarget(string packageName)
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(ActionStatus.NoTarget, await backend.RemoveProvisionedAppxAsync(Image, packageName, Ct));
    }

    /// <summary>
    /// A no-target must not silently succeed, and must not run the removal either. Firing
    /// /Remove-ProvisionedAppxPackage at a name that is not there is how a build report ends up
    /// claiming work it never did.
    /// </summary>
    [Fact]
    public async Task A_NoTarget_removal_never_invokes_dism()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        await backend.RemoveProvisionedAppxAsync(Image, "Microsoft.XboxApp", Ct);

        Assert.Equal(0, runner.CountMatching("/Remove-ProvisionedAppxPackage"));
    }

    [Fact]
    public async Task Appx_that_is_present_reports_Applied()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.Applied,
            await backend.RemoveProvisionedAppxAsync(Image, "Microsoft.BingNews", Ct));
    }

    /// <summary>
    /// The catalog names <c>Microsoft.BingNews</c>; DISM only accepts the versioned full name. The
    /// backend resolves one to the other so the catalog does not carry version strings that change
    /// with every servicing update.
    /// </summary>
    [Fact]
    public async Task A_family_name_resolves_to_the_full_package_name()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        await backend.RemoveProvisionedAppxAsync(Image, "Microsoft.BingNews", Ct);

        Assert.Contains(
            runner.CommandLines,
            c => c.Contains(
                @"/PackageName:""Microsoft.BingNews_4.1.24002.0_neutral_~_8wekyb3d8bbwe""",
                StringComparison.Ordinal));
    }

    /// <summary>A prefix must not match a longer sibling: "Microsoft.Bing" is not "Microsoft.BingNews".</summary>
    [Fact]
    public async Task A_partial_family_name_does_not_match()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(ActionStatus.NoTarget, await backend.RemoveProvisionedAppxAsync(Image, "Microsoft.Bing", Ct));
    }

    [Fact]
    public async Task A_removed_appx_is_NoTarget_the_second_time()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.Applied, await backend.RemoveProvisionedAppxAsync(Image, "Microsoft.BingNews", Ct));
        Assert.Equal(
            ActionStatus.NoTarget, await backend.RemoveProvisionedAppxAsync(Image, "Microsoft.BingNews", Ct));
        Assert.Equal(1, runner.CountMatching("/Remove-ProvisionedAppxPackage"));
    }

    [Fact]
    public async Task A_capability_that_is_not_present_reports_NoTarget()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        // Print.Fax.Scan is listed with State "Not Present" on stock 26200 media.
        Assert.Equal(
            ActionStatus.NoTarget,
            await backend.RemoveCapabilityAsync(Image, "Print.Fax.Scan~~~~0.0.1.0", Ct));
        Assert.Equal(0, runner.CountMatching("/Remove-Capability"));
    }

    /// <summary>
    /// Worth pinning because I guessed wrong before the real capture arrived: Internet Explorer is
    /// still <c>Installed</c> on 26200, not <c>Not Present</c>. Removing it is real work.
    /// </summary>
    [Fact]
    public async Task Internet_explorer_is_still_installed_on_26200()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.Applied,
            await backend.RemoveCapabilityAsync(Image, "Browser.InternetExplorer~~~~0.0.11.0", Ct));
    }

    /// <summary>
    /// The ambiguity real media exposed. <c>/Get-Packages</c> lists this base name twice — Staged at
    /// 10.0.26100.1742 and Installed at 10.0.26100.8036, in that order. Resolving a short catalog
    /// name by taking the first prefix match would pick the Staged one and act on the wrong version;
    /// the installed identity has to win regardless of DISM's ordering.
    /// </summary>
    [Fact]
    public async Task A_short_package_name_resolves_to_the_installed_identity_not_the_staged_one()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        var status = await backend.RemovePackageAsync(
            Image, "Microsoft-OneCore-ApplicationModel-Sync-Desktop-FOD-Package", Ct);

        Assert.Equal(ActionStatus.Applied, status);
        Assert.Contains(
            runner.CommandLines,
            c => c.Contains("~~10.0.26100.8036", StringComparison.Ordinal));
        Assert.DoesNotContain(
            runner.CommandLines,
            c => c.Contains("~~10.0.26100.1742", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same hazard for capabilities: ~100 <c>Language.Basic~~~&lt;locale&gt;</c> identities are
    /// listed and exactly one is installed. A short name must find it rather than the first locale
    /// in the list.
    /// </summary>
    [Fact]
    public async Task A_short_capability_name_finds_the_one_installed_locale()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(ActionStatus.Applied, await backend.RemoveCapabilityAsync(Image, "Language.Basic", Ct));
        Assert.Contains(
            runner.CommandLines,
            c => c.Contains(@"/CapabilityName:""Language.Basic~~~en-US~0.0.1.0""", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_capability_that_is_not_listed_at_all_reports_NoTarget()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.NoTarget,
            await backend.RemoveCapabilityAsync(Image, "Nonexistent.Capability~~~~1.0.0.0", Ct));
    }

    [Fact]
    public async Task An_installed_capability_reports_Applied()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.Applied,
            await backend.RemoveCapabilityAsync(Image, "Media.WindowsMediaPlayer~~~~0.0.12.0", Ct));
    }

    /// <summary>
    /// Capability identities embed a version — <c>~~~~0.0.12.0</c> — that moves between builds, so
    /// the suffix is optional and resolved against what DISM actually listed.
    /// </summary>
    [Fact]
    public async Task A_capability_resolves_without_its_version_suffix()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.Applied, await backend.RemoveCapabilityAsync(Image, "Media.WindowsMediaPlayer", Ct));
        Assert.Contains(
            runner.CommandLines,
            c => c.Contains(@"/CapabilityName:""Media.WindowsMediaPlayer~~~~0.0.12.0""", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_feature_reports_NoTarget()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.NoTarget, await backend.DisableFeatureAsync(Image, "NoSuchFeature", false, Ct));
        Assert.Equal(0, runner.CountMatching("/Disable-Feature"));
    }

    [Fact]
    public async Task An_enabled_feature_reports_Applied()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.Applied, await backend.DisableFeatureAsync(Image, "WorkFolders-Client", false, Ct));
    }

    [Fact]
    public async Task A_feature_already_disabled_is_NoTarget_when_no_payload_removal_is_asked_for()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.NoTarget,
            await backend.DisableFeatureAsync(Image, "Printing-XPSServices-Features", removePayload: false, Ct));
    }

    /// <summary>
    /// Disabled-but-payload-present is not a no-op when the payload is the point: removing it
    /// reclaims real space, so it must report Applied.
    /// </summary>
    [Fact]
    public async Task A_disabled_feature_still_carrying_payload_is_real_work()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.Applied,
            await backend.DisableFeatureAsync(Image, "Printing-XPSServices-Features", removePayload: true, Ct));
        Assert.Contains(runner.CommandLines, c => c.Contains("/Remove", StringComparison.Ordinal));
    }

    /// <summary>NetFx3 is the only payload-removed feature on stock 26200 media.</summary>
    [Fact]
    public async Task A_feature_already_stripped_of_its_payload_is_NoTarget()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.NoTarget,
            await backend.DisableFeatureAsync(Image, "NetFx3", removePayload: true, Ct));
    }

    [Fact]
    public async Task An_absent_package_reports_NoTarget()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.NoTarget,
            await backend.RemovePackageAsync(
                Image, "Microsoft-Windows-NotShipped-Package~31bf3856ad364e35~amd64~~1.0", Ct));
    }

    [Fact]
    public async Task An_installed_package_reports_Applied()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.Applied,
            await backend.RemovePackageAsync(
                Image,
                "Microsoft-OneCore-ApplicationModel-Sync-Desktop-FOD-Package~31bf3856ad364e35~amd64~~10.0.26100.8036",
                Ct));
    }

    /// <summary>
    /// The enumeration is the primary check, but DISM can still disagree — a component the listing
    /// showed can be gone by the time the removal runs. That is a no-op, not a build failure.
    /// </summary>
    [Fact]
    public async Task A_missing_target_error_from_dism_is_reported_as_NoTarget_not_a_failure()
    {
        var runner = FullyStockedRunner();
        runner.RespondWithExitCode("/Remove-Capability", unchecked((int)0x800F080C));
        using var backend = NewBackend(runner);

        Assert.Equal(
            ActionStatus.NoTarget,
            await backend.RemoveCapabilityAsync(Image, "Media.WindowsMediaPlayer~~~~0.0.12.0", Ct));
    }

    [Fact]
    public async Task A_real_failure_from_a_removal_still_throws()
    {
        var runner = FullyStockedRunner();
        runner.RespondWithExitCode("/Remove-Capability", 5);
        using var backend = NewBackend(runner);

        await Assert.ThrowsAsync<DismException>(
            () => backend.RemoveCapabilityAsync(Image, "Media.WindowsMediaPlayer~~~~0.0.12.0", Ct));
    }

    // ---------------------------------------------------------------- caching

    /// <summary>
    /// A balanced preset runs on the order of a hundred actions. Enumerating per action would mean a
    /// hundred extra DISM invocations, each paying full process and provider-store startup.
    /// </summary>
    [Fact]
    public async Task The_image_is_enumerated_once_no_matter_how_many_actions_run()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        for (var i = 0; i < 10; i++)
        {
            await backend.RemoveProvisionedAppxAsync(Image, "Microsoft.XboxApp", Ct);
            await backend.RemoveCapabilityAsync(Image, "Nonexistent~~~~1.0", Ct);
            await backend.DisableFeatureAsync(Image, "NoSuchFeature", false, Ct);
            await backend.RemovePackageAsync(Image, "No-Such-Package~31bf3856ad364e35~amd64~~1.0", Ct);
        }

        Assert.Equal(1, runner.CountMatching("/Get-ProvisionedAppxPackages"));
        Assert.Equal(1, runner.CountMatching("/Get-Capabilities"));
        Assert.Equal(1, runner.CountMatching("/Get-Features"));
        Assert.Equal(1, runner.CountMatching("/Get-Packages"));
    }

    /// <summary>
    /// /Cleanup-Image removes superseded and staged components on its own, so anything cached
    /// beforehand is no longer trustworthy.
    /// </summary>
    [Fact]
    public async Task Cleanup_invalidates_the_cached_inventory()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        await backend.RemovePackageAsync(Image, "No-Such-Package~31bf3856ad364e35~amd64~~1.0", Ct);
        await backend.CleanupImageAsync(Image, resetBase: true, cancellationToken: Ct);
        await backend.RemovePackageAsync(Image, "No-Such-Package~31bf3856ad364e35~amd64~~1.0", Ct);

        Assert.Equal(2, runner.CountMatching("/Get-Packages"));
    }

    // ---------------------------------------------------------------- failure mapping

    [Fact]
    public async Task Error_740_surfaces_as_an_actionable_elevation_exception()
    {
        var runner = new FakeProcessRunner
        {
            Fallback = new ProcessRunResult(740, [.. Samples.Error740.Split('\n')], false),
        };
        using var backend = NewBackend(runner);

        var exception = await Assert.ThrowsAsync<DismElevationRequiredException>(
            () => backend.GetEditionsAsync(@"D:\sources\install.wim", Ct));

        Assert.Contains("Administrator", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Get-WimInfo", exception.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reboot_required_is_not_treated_as_a_failure()
    {
        var runner = FullyStockedRunner();
        runner.RespondWithExitCode("/Cleanup-Image", 3010);
        using var backend = NewBackend(runner);

        await backend.CleanupImageAsync(Image, resetBase: true, cancellationToken: Ct);
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task Cancellation_propagates_from_a_long_operation()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => backend.CleanupImageAsync(Image, resetBase: true, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Long_operations_ask_for_the_child_to_be_killed_on_cancellation()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        await backend.CleanupImageAsync(Image, resetBase: true, cancellationToken: Ct);
        await backend.ExportImageAsync(
            @"C:\a.wim", 1, @"C:\b.wim", CompressionType.Recovery, cancellationToken: Ct);

        Assert.All(runner.Requests, r => Assert.True(r.KillOnCancel));
    }

    /// <summary>
    /// The one operation that must never be interrupted. Killing DISM part-way through writing a WIM
    /// back produces the corrupt image the whole design exists to avoid, and it is not recoverable
    /// by /Cleanup-Mountpoints.
    /// </summary>
    [Fact]
    public async Task Unmount_refuses_to_be_killed_mid_write()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        await backend.UnmountAsync(Image, commit: true, cancellationToken: Ct);

        var unmount = runner.Requests.Single(r => r.Arguments.Contains("/Unmount-Wim", StringComparison.Ordinal));
        Assert.False(unmount.KillOnCancel);
    }

    /// <summary>
    /// The unwind path itself. If the token were honoured here, the very call that discards a
    /// cancelled build's changes would be cancelled — leaving the image mounted.
    /// </summary>
    [Fact]
    public async Task A_discard_unmount_runs_even_when_already_cancelled()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await backend.UnmountAsync(Image, commit: false, cancellationToken: cts.Token);

        Assert.Contains(runner.CommandLines, c => c.Contains("/Discard", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unmounting_forgets_the_cached_inventory()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);

        await backend.RemoveProvisionedAppxAsync(Image, "Microsoft.XboxApp", Ct);
        await backend.UnmountAsync(Image, commit: true, cancellationToken: Ct);
        await backend.RemoveProvisionedAppxAsync(Image, "Microsoft.XboxApp", Ct);

        Assert.Equal(2, runner.CountMatching("/Get-ProvisionedAppxPackages"));
    }

    // ---------------------------------------------------------------- progress

    [Fact]
    public async Task Mounting_reports_a_start_and_a_finish()
    {
        var runner = FullyStockedRunner();
        using var backend = NewBackend(runner);
        var reported = new List<double>();

        var image = await backend.MountAsync(
            @"C:\work\install.wim", 6, @"C:\TinyWin\mount", new Sink<double>(reported.Add), Ct);

        Assert.Equal(@"C:\TinyWin\mount", image.MountPath);
        Assert.Equal(0.0, reported[0]);
        Assert.Equal(1.0, reported[^1]);
    }

    /// <summary>
    /// The degradation the spike asked for: when DISM emits no percentages, progress must still
    /// begin, move, and end — not sit at zero for forty minutes.
    /// </summary>
    [Fact]
    public async Task An_operation_with_no_percentages_still_reports_a_start_and_an_end()
    {
        var runner = FullyStockedRunner();
        runner.Respond("/Cleanup-Image", "Deployment Image Servicing and Management tool\nImage Version: 10.0.26200.8037");
        using var backend = NewBackend(runner);
        var reported = new List<double>();

        await backend.CleanupImageAsync(Image, resetBase: true, new Sink<double>(reported.Add), Ct);

        Assert.Equal(0.0, reported[0]);
        Assert.Equal(1.0, reported[^1]);
        Assert.Contains(reported, v => v > 0.0 && v < 1.0);
    }

    [Fact]
    public async Task The_log_sink_receives_dism_output()
    {
        var runner = FullyStockedRunner();
        var logged = new List<string>();
        using var backend = NewBackend(runner);
        backend.Log = new Sink<string>(logged.Add);

        await backend.GetMountedImagesAsync(Ct);

        Assert.Contains(logged, l => l.Contains("Mount Dir", StringComparison.Ordinal));
    }

    private sealed class Sink<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
