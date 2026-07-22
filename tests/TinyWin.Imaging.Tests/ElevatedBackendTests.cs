using System.Runtime.Versioning;
using System.Security.Principal;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Imaging;

namespace TinyWin.Imaging.Tests;

/// <summary>
/// Drives the real <see cref="DismExeBackend"/> against real media. Skipped unless elevated and
/// pointed at a WIM.
/// </summary>
/// <remarks>
/// <para>Everything else in this project tests the two pure halves — command-line construction and
/// output parsing — behind <c>IProcessRunner</c>. That is the only thing testable without
/// elevation, because <c>dism.exe</c> refuses every command with 740 otherwise. These tests close
/// the remaining gap: that the command lines are ones real DISM accepts, and that real DISM output
/// is shaped the way the samples say it is.</para>
///
/// <para>Run them with <c>scripts\Verify-DismBackend.ps1</c> from an elevated prompt. They skip
/// rather than fail when the environment is not set up, so CI stays green — the CI runner is
/// elevated but has no Windows media.</para>
///
/// <list type="table">
///   <item><term><c>TINYWIN_ELEVATED_WIM</c></term><description>Path to an install.wim. Enables the
///     read-only tests. The WIM is never modified — it is mounted <c>/ReadOnly</c> and unmounted
///     with <c>/Discard</c>.</description></item>
///   <item><term><c>TINYWIN_ELEVATED_RW_WIM</c></term><description>Path to a <b>scratch copy</b> of a
///     WIM. Enables the removal tests, which mount read/write. Never point this at media you care
///     about.</description></item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public class ElevatedBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Held in a field so the "always pass the test token" analyzer does not fire on the unwind path.</summary>
    private static readonly CancellationToken Uncancellable = CancellationToken.None;

    private static bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static string RequireWim()
    {
        var wim = Environment.GetEnvironmentVariable("TINYWIN_ELEVATED_WIM");

        if (!IsElevated)
        {
            Assert.Skip("Not elevated. DISM refuses every command with error 740 — see scripts/Verify-DismBackend.ps1.");
        }

        if (string.IsNullOrWhiteSpace(wim) || !File.Exists(wim))
        {
            Assert.Skip("TINYWIN_ELEVATED_WIM is not set to an existing WIM.");
        }

        return wim;
    }

    private static string RequireWritableWim()
    {
        RequireWim();
        var wim = Environment.GetEnvironmentVariable("TINYWIN_ELEVATED_RW_WIM");

        if (string.IsNullOrWhiteSpace(wim) || !File.Exists(wim))
        {
            Assert.Skip("TINYWIN_ELEVATED_RW_WIM is not set to an existing scratch WIM.");
        }

        return wim;
    }

    private static string NewMountDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tinywin-verify", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// The command lines this backend builds are accepted by real DISM, and the real output parses
    /// into the model. Failure here means the golden tests are golden against the wrong thing.
    /// </summary>
    [Fact]
    public async Task Real_dism_returns_editions_that_parse()
    {
        var wim = RequireWim();
        using var backend = new DismExeBackend();

        var editions = await backend.GetEditionsAsync(wim, Ct);

        Assert.NotEmpty(editions);
        Assert.All(editions, e =>
        {
            Assert.True(e.Index > 0, "Every edition needs an index.");
            Assert.False(string.IsNullOrWhiteSpace(e.Name), $"Index {e.Index} parsed with no name.");
            Assert.False(string.IsNullOrWhiteSpace(e.EditionId), $"Index {e.Index} parsed with no edition id.");
            Assert.False(string.IsNullOrWhiteSpace(e.Architecture), $"Index {e.Index} parsed with no architecture.");
            Assert.True(e.Build > 0, $"Index {e.Index} parsed with no build number.");
            Assert.True(e.SizeBytes > 0, $"Index {e.Index} parsed with no size.");
        });
    }

    [Fact]
    public async Task Real_dism_reports_a_supported_build()
    {
        var wim = RequireWim();
        using var backend = new DismExeBackend();

        var editions = await backend.GetEditionsAsync(wim, Ct);

        Assert.NotEqual(MediaSupport.Unsupported, MediaSupportPolicy.Classify(editions[0].Build));
    }

    /// <summary>Preflight and crash recovery. Both must work on a clean machine with nothing mounted.</summary>
    [Fact]
    public async Task Mount_point_enumeration_and_cleanup_succeed()
    {
        RequireWim();
        using var backend = new DismExeBackend();

        await backend.GetMountedImagesAsync(Ct);
        await backend.CleanupMountPointsAsync(Ct);
    }

    /// <summary>
    /// Answers the question the spike left open (docs/spikes/dism-backend.md §5): does DISM emit a
    /// percentage when stdout is a pipe? This test never fails on the answer — it records it. Either
    /// outcome is handled; what matters is that progress is not silently stuck at zero.
    /// </summary>
    [Fact]
    public async Task Mounting_read_write_reports_progress_or_at_least_stage_transitions()
    {
        var wim = RequireWritableWim();
        var mount = NewMountDirectory();
        using var backend = new DismExeBackend();

        var reported = new List<double>();
        var logged = new List<string>();
        backend.Log = new Sink<string>(logged.Add);

        MountedImage? image = null;
        try
        {
            image = await backend.MountAsync(wim, 1, mount, new Sink<double>(reported.Add), Ct);

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"progress reports: {reported.Count}, distinct values: {reported.Distinct().Count()}");
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"intermediate percentages seen: {reported.Any(v => v > 0.15 && v < 1.0)}");
            TestContext.Current.TestOutputHelper?.WriteLine(
                string.Join(Environment.NewLine, logged.Take(20)));

            Assert.Equal(0.0, reported[0]);
            Assert.Equal(1.0, reported[^1]);
        }
        finally
        {
            if (image is not null)
            {
                // Explicitly uncancellable: this discard is the unwind, and it has to run even when
                // the test run is being torn down. Leaving an image mounted needs a reboot to fix.
                await backend.UnmountAsync(image, commit: false, cancellationToken: Uncancellable);
            }

            Directory.Delete(mount, recursive: true);
        }
    }

    /// <summary>
    /// The whole point of the design, against a real image: a package that is present reports
    /// Applied, and one that no longer ships on this build reports NoTarget rather than a silent
    /// success. Mounts read/write but always unmounts with <c>/Discard</c>.
    /// </summary>
    [Fact]
    public async Task Removal_distinguishes_applied_from_no_target_on_a_real_image()
    {
        var wim = RequireWritableWim();
        var mount = NewMountDirectory();
        using var backend = new DismExeBackend();

        MountedImage? image = null;
        try
        {
            image = await backend.MountAsync(wim, 1, mount, null, Ct);

            var installed = await backend.GetProvisionedAppxAsync(image, Ct);
            Assert.NotEmpty(installed);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{installed.Count} provisioned packages:{Environment.NewLine}" +
                string.Join(Environment.NewLine, installed.Select(p => "  " + p.PackageName)));

            // Removed from Windows years before 26100 — the catalog-drift case NoTarget exists for.
            Assert.Equal(
                ActionStatus.NoTarget,
                await backend.RemoveProvisionedAppxAsync(image, "Microsoft.3DBuilder", Ct));

            var present = installed[0];
            Assert.Equal(
                ActionStatus.Applied,
                await backend.RemoveProvisionedAppxAsync(image, present.PackageName, Ct));

            // And it is gone the second time round.
            Assert.Equal(
                ActionStatus.NoTarget,
                await backend.RemoveProvisionedAppxAsync(image, present.PackageName, Ct));

            var capabilities = await backend.RemoveCapabilityAsync(image, "Nonexistent.Capability~~~~1.0.0.0", Ct);
            Assert.Equal(ActionStatus.NoTarget, capabilities);

            Assert.Equal(
                ActionStatus.NoTarget,
                await backend.DisableFeatureAsync(image, "NoSuchFeatureName", removePayload: false, Ct));
        }
        finally
        {
            if (image is not null)
            {
                // Discard, always. Nothing this test does may reach the media.
                // Explicitly uncancellable: this discard is the unwind, and it has to run even when
                // the test run is being torn down. Leaving an image mounted needs a reboot to fix.
                await backend.UnmountAsync(image, commit: false, cancellationToken: Uncancellable);
            }

            Directory.Delete(mount, recursive: true);
        }
    }

    private sealed class Sink<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
