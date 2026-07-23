using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;
using TinyWin.Core.Recovery;

namespace TinyWin.Tests;

/// <summary>
/// The <c>state.json</c> of docs/PLAN.md section 2.2, on its own.
/// </summary>
/// <remarks>
/// The moment this file matters is the moment the process dies, so the failure modes worth testing
/// are the ugly ones: a half-written file, a file from an older schema, a file for another build.
/// All three must read as "nothing to resume" or be refused by name — never as a checkpoint that
/// looks usable and is not.
/// </remarks>
public sealed class CheckpointStoreTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "tinywin-cp-" + Guid.NewGuid().ToString("N"));

    public CheckpointStoreTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static BuildRequest Request(string scratch) => new()
    {
        SourceIsoPath = Path.Combine(scratch, "source.iso"),
        OutputIsoPath = Path.Combine(scratch, "out.iso"),
        EditionIndex = 6,
        ComponentIds = ["apps.xbox", "apps.onedrive"],
        ScratchDirectory = scratch,
    };

    private BuildCheckpoint Sample() => new()
    {
        Fingerprint = BuildCheckpoint.FingerprintOf(Request(_scratch)),
        UpdatedUtc = new DateTimeOffset(2026, 7, 22, 10, 30, 0, TimeSpan.Zero),
        SourceIsoPath = Path.Combine(_scratch, "source.iso"),
        CompletedStages = [BuildStageId.Preflight, BuildStageId.StageFiles, BuildStageId.InspectIso],
        StagedIsoDirectory = Path.Combine(_scratch, "iso"),
        InstallWimPath = Path.Combine(_scratch, "iso", "sources", "install.wim"),
        EditionIndexOverride = 1,
        BootGeometry = new IsoBootGeometry
        {
            VolumeId = "CCCOMA_X64FRE_EN-US_DV9",
            BiosBootImage = @"boot\etfsboot.com",
            BiosLoadSize = 8,
            UefiBootImage = @"efi\microsoft\boot\efisys.bin",
            UefiLoadSize = 1,
        },
        Outcomes =
        [
            new ActionOutcome
            {
                ComponentId = "apps.xbox",
                Description = "Remove app package Microsoft.XboxApp",
                Status = ActionStatus.Applied,
                Stage = BuildStageId.ApplyComponents,
            },
        ],
        Warnings = ["Found 1 image(s) left mounted by a previous run"],
    };

    [Fact]
    public async Task A_checkpoint_round_trips_including_the_boot_geometry()
    {
        var store = new JsonCheckpointStore(_scratch);
        await store.SaveAsync(Sample(), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(Sample().Fingerprint, loaded.Fingerprint);
        Assert.Equal(3, loaded.CompletedStages.Count);
        Assert.Equal(BuildStageId.InspectIso, loaded.CompletedStages[2]);
        Assert.Equal(8, loaded.BootGeometry!.BiosLoadSize);
        Assert.Equal(BuildStageId.ApplyComponents, loaded.Outcomes[0].Stage);
        Assert.Single(loaded.Warnings);
    }

    [Fact]
    public async Task No_file_means_nothing_to_resume() =>
        Assert.Null(await new JsonCheckpointStore(_scratch).LoadAsync(TestContext.Current.CancellationToken));

    /// <summary>A file truncated by the crash it was recording must not read as a valid state.</summary>
    [Fact]
    public async Task A_truncated_file_reads_as_no_checkpoint()
    {
        var store = new JsonCheckpointStore(_scratch);
        await store.SaveAsync(Sample(), TestContext.Current.CancellationToken);

        var text = await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            store.FilePath, text[..(text.Length / 2)], TestContext.Current.CancellationToken);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_checkpoint_from_another_schema_version_is_ignored()
    {
        var store = new JsonCheckpointStore(_scratch);
        await store.SaveAsync(Sample() with { SchemaVersion = 99 }, TestContext.Current.CancellationToken);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_is_safe_when_there_is_nothing_to_delete()
    {
        var store = new JsonCheckpointStore(_scratch);
        await store.DeleteAsync(TestContext.Current.CancellationToken);

        await store.SaveAsync(Sample(), TestContext.Current.CancellationToken);
        await store.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(store.FilePath));
    }

    /// <summary>
    /// Same path, different ISO. The fingerprint has to notice, or a 24H2 build resumes onto 25H2
    /// media and produces an image blended from both.
    /// </summary>
    [Fact]
    public async Task The_fingerprint_changes_when_the_source_iso_contents_change()
    {
        var iso = Path.Combine(_scratch, "source.iso");
        await File.WriteAllBytesAsync(iso, new byte[1024], TestContext.Current.CancellationToken);
        var before = BuildCheckpoint.FingerprintOf(Request(_scratch));

        await File.WriteAllBytesAsync(iso, new byte[2048], TestContext.Current.CancellationToken);
        var after = BuildCheckpoint.FingerprintOf(Request(_scratch));

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void The_fingerprint_ignores_the_order_components_were_selected_in()
    {
        var a = BuildCheckpoint.FingerprintOf(Request(_scratch) with { ComponentIds = ["a", "b", "c"] });
        var b = BuildCheckpoint.FingerprintOf(Request(_scratch) with { ComponentIds = ["c", "a", "b"] });

        Assert.Equal(a, b);
    }

    [Fact]
    public void The_fingerprint_changes_when_the_selection_does()
    {
        var a = BuildCheckpoint.FingerprintOf(Request(_scratch) with { ComponentIds = ["a", "b"] });
        var b = BuildCheckpoint.FingerprintOf(Request(_scratch) with { ComponentIds = ["a", "b", "c"] });

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Only the outcomes of stages being reused are restored. The rest are about to be produced
    /// again, and restoring them would double every count in the report.
    /// </summary>
    [Fact]
    public void Restoring_keeps_only_the_outcomes_of_the_stages_being_reused()
    {
        var checkpoint = Sample() with
        {
            Outcomes =
            [
                new ActionOutcome
                {
                    ComponentId = "apps.xbox",
                    Description = "kept",
                    Status = ActionStatus.Applied,
                    Stage = BuildStageId.ApplyComponents,
                },
                new ActionOutcome
                {
                    ComponentId = "privacy.telemetry",
                    Description = "dropped",
                    Status = ActionStatus.Applied,
                    Stage = BuildStageId.ApplyRegistry,
                },
            ],
        };

        var context = new BuildContext
        {
            Request = Request(_scratch),
            Plan = Fakes.PipelineHarness.Plan(),
        };

        checkpoint.RestoreInto(context, new HashSet<BuildStageId> { BuildStageId.ApplyComponents });

        Assert.Equal("kept", Assert.Single(context.Outcomes).Description);
        Assert.Equal(Path.Combine(_scratch, "iso"), context.StagedIsoDirectory);
        Assert.Equal(1, context.EditionIndexOverride);
    }
}
