using TinyWin.Catalog.Models;
using TinyWin.Catalog.Resolution;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;
using TinyWin.Core.Pipeline.Stages;
using TinyWin.Tests.Fakes;

namespace TinyWin.Tests;

public sealed class PatchBootWimStageTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "tinywin-boot-" + Guid.NewGuid().ToString("N"));

    public PatchBootWimStageTests()
    {
        Directory.CreateDirectory(Path.Combine(_scratch, "iso", "sources"));
        File.WriteAllText(Path.Combine(_scratch, "iso", "sources", "boot.wim"), "stub");
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private BuildContext Context(UnattendOptions unattend) => new()
    {
        Request = new BuildRequest
        {
            SourceIsoPath = "in.iso",
            OutputIsoPath = "out.iso",
            EditionIndex = 1,
            ComponentIds = [],
            ScratchDirectory = _scratch,
            Unattend = unattend,
        },
        Plan = new ResolvedPlan
        {
            ComponentIds = [],
            ImageActions = [],
            RegistryActions = [],
            Warnings = [],
        },
        StagedIsoDirectory = Path.Combine(_scratch, "iso"),
    };

    /// <summary>
    /// The bypasses must land in boot.wim index 2 — index 1 is Windows PE, index 2 is Setup, and
    /// only Setup runs the compatibility checks.
    /// </summary>
    [Fact]
    public async Task Patches_boot_wim_index_2_and_commits()
    {
        var imaging = new FakeImagingBackend();
        var registry = new FakeOfflineRegistry();

        await new PatchBootWimStage(imaging, registry).ExecuteAsync(
            Context(new UnattendOptions()), new Progress<BuildProgress>(), TestContext.Current.CancellationToken);

        Assert.Contains(imaging.Calls, c => c.Contains("boot.wim#2", StringComparison.Ordinal));
        Assert.Contains(imaging.Calls, c => c.Contains("commit: True", StringComparison.Ordinal));
        Assert.Empty(imaging.Mounted);
    }

    [Fact]
    public async Task Writes_the_LabConfig_and_MoSetup_bypasses()
    {
        var imaging = new FakeImagingBackend();
        var registry = new FakeOfflineRegistry();

        await new PatchBootWimStage(imaging, registry).ExecuteAsync(
            Context(new UnattendOptions()), new Progress<BuildProgress>(), TestContext.Current.CancellationToken);

        Assert.Contains(registry.Applied, a => a.Key == @"Setup\LabConfig" && a.ValueName == "BypassTPMCheck");
        Assert.Contains(registry.Applied, a => a.Key == @"Setup\LabConfig" && a.ValueName == "BypassSecureBootCheck");
        Assert.Contains(registry.Applied, a => a.Key == @"Setup\LabConfig" && a.ValueName == "BypassRAMCheck");
        Assert.Contains(registry.Applied, a => a.Key == @"Setup\LabConfig" && a.ValueName == "BypassStorageCheck");
        Assert.Contains(registry.Applied, a => a.Key == @"Setup\LabConfig" && a.ValueName == "BypassCPUCheck");
        Assert.Contains(
            registry.Applied,
            a => a.Key == @"Setup\MoSetup" && a.ValueName == "AllowUpgradesWithUnsupportedTPMOrCPU");

        Assert.All(registry.Applied, a => Assert.Equal(RegistryHive.System, a.Hive));
    }

    [Fact]
    public async Task Only_the_requested_bypasses_are_written()
    {
        var imaging = new FakeImagingBackend();
        var registry = new FakeOfflineRegistry();

        var options = new UnattendOptions
        {
            BypassTpmCheck = true,
            BypassSecureBootCheck = false,
            BypassRamCheck = false,
            BypassCpuCheck = false,
            BypassStorageCheck = false,
        };

        await new PatchBootWimStage(imaging, registry).ExecuteAsync(
            Context(options), new Progress<BuildProgress>(), TestContext.Current.CancellationToken);

        Assert.Contains(registry.Applied, a => a.ValueName == "BypassTPMCheck");
        Assert.DoesNotContain(registry.Applied, a => a.ValueName == "BypassRAMCheck");
    }

    [Fact]
    public void Stage_is_skipped_when_no_bypass_is_requested()
    {
        var options = new UnattendOptions
        {
            BypassTpmCheck = false,
            BypassSecureBootCheck = false,
            BypassRamCheck = false,
            BypassCpuCheck = false,
            BypassStorageCheck = false,
        };

        var stage = new PatchBootWimStage(new FakeImagingBackend(), new FakeOfflineRegistry());
        Assert.False(stage.ShouldRun(Context(options)));
    }

    /// <summary>
    /// If the hive work throws, boot.wim must be discarded rather than left mounted — a mounted
    /// boot.wim locks the file and the ISO can never be built.
    /// </summary>
    [Fact]
    public async Task Hive_failure_discards_the_mount_instead_of_leaving_it_open()
    {
        var imaging = new FakeImagingBackend();
        var registry = new FakeOfflineRegistry { OpenFailure = new IOException("hive is locked") };

        await Assert.ThrowsAsync<IOException>(() =>
            new PatchBootWimStage(imaging, registry).ExecuteAsync(
                Context(new UnattendOptions()), new Progress<BuildProgress>(), TestContext.Current.CancellationToken));

        Assert.Contains(imaging.Calls, c => c.Contains("commit: False", StringComparison.Ordinal));
        Assert.Empty(imaging.Mounted);
    }

    [Fact]
    public async Task Missing_boot_wim_warns_rather_than_failing_the_build()
    {
        File.Delete(Path.Combine(_scratch, "iso", "sources", "boot.wim"));

        var imaging = new FakeImagingBackend();
        var context = Context(new UnattendOptions());

        await new PatchBootWimStage(imaging, new FakeOfflineRegistry()).ExecuteAsync(
            context, new Progress<BuildProgress>(), TestContext.Current.CancellationToken);

        Assert.Contains(context.Warnings, w => w.Contains("boot.wim", StringComparison.Ordinal));
        Assert.Empty(imaging.Calls);
    }
}
