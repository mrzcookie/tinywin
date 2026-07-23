using TinyWin.Core.Diagnostics;
using TinyWin.Core.Pipeline;
using TinyWin.Tests.Fakes;

namespace TinyWin.Tests;

/// <summary>
/// The progressive free-space guards of docs/PLAN.md section 2.2.
/// </summary>
/// <remarks>
/// Preflight's single 25 GB check is necessary and not sufficient: staging, the ESD export, the
/// recompress and the ISO write each consume gigabytes at different points, and the ISO usually
/// lands on a different volume from the scratch directory. Running out mid-DISM produces a
/// truncated WIM and an error message pointing nowhere near the cause, so each consuming stage
/// checks what it is about to need and fails with a sentence instead.
/// </remarks>
public sealed class DiskSpaceGuardTests
{
    private const long OneGb = 1024L * 1024 * 1024;

    [Fact]
    public async Task Preflight_refuses_a_scratch_volume_that_cannot_hold_a_build()
    {
        using var harness = new PipelineHarness();
        harness.Environment.DefaultFreeBytes = 5 * OneGb;

        var report = await harness.RunAsync(harness.Context(), TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);

        var failure = Assert.Single(report.Stages, s => s.State == StageState.Failed);
        Assert.Equal(BuildStageId.Preflight, failure.Stage);
        Assert.Contains("25 GB", failure.Error, StringComparison.Ordinal);
        Assert.Contains("Free up", failure.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The output ISO is written next to the source, not into the scratch directory, so a roomy
    /// scratch volume says nothing about whether the result fits.
    /// </summary>
    [Fact]
    public async Task Preflight_checks_the_output_volume_separately_from_the_scratch_volume()
    {
        using var harness = new PipelineHarness();
        harness.Environment.DefaultFreeBytes = 500 * OneGb;
        harness.Environment.FreeBytesByPrefix[Path.GetDirectoryName(harness.OutputIsoPath)!] = 1024;

        var report = await harness.RunAsync(harness.Context(), TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);

        var failure = Assert.Single(report.Stages, s => s.State == StageState.Failed);
        Assert.Equal(BuildStageId.Preflight, failure.Stage);
        Assert.Contains("finished ISO", failure.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Preflight passes, then the volume fills before the copy — the case a single up-front check
    /// cannot catch.
    /// </summary>
    [Fact]
    public async Task Staging_re_checks_after_preflight_passed()
    {
        using var harness = new PipelineHarness();

        var calls = 0;
        var environment = new ShrinkingEnvironment(() => ++calls <= 2 ? 500 * OneGb : 1024);

        var pipeline = BuildPipelineFactory.Create(
            harness.Imaging, harness.Registry, harness.IsoBuilder, harness.Unattend,
            harness.ScratchDirectory, environment);

        var report = await pipeline.RunAsync(
            harness.Context(), new Progress<BuildProgress>(), TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);

        var failure = Assert.Single(report.Stages, s => s.State == StageState.Failed);
        Assert.Equal(BuildStageId.StageFiles, failure.Stage);
        Assert.Contains("Copying the ISO contents", failure.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_names_the_volume_the_shortfall_and_the_way_out()
    {
        var environment = new FakeBuildEnvironment { DefaultFreeBytes = 2 * OneGb };

        var ex = Assert.Throws<InsufficientDiskSpaceException>(() =>
            DiskSpace.Require(environment, @"C:\scratch", 10 * OneGb, "Recompressing the image"));

        Assert.Contains("Recompressing the image", ex.Message, StringComparison.Ordinal);
        Assert.Contains("10 GB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 GB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Free up 8 GB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scratch directory", ex.Message, StringComparison.Ordinal);
        Assert.Equal(10 * OneGb, ex.RequiredBytes);
    }

    [Fact]
    public void Enough_space_passes_silently()
    {
        var environment = new FakeBuildEnvironment { DefaultFreeBytes = 50 * OneGb };
        DiskSpace.Require(environment, @"C:\scratch", 10 * OneGb, "Anything");
    }

    /// <summary>
    /// An unmeasurable volume — a UNC scratch path — must not block a build. Refusing to run
    /// because a counter could not be read would be a worse failure than the one being prevented.
    /// </summary>
    [Fact]
    public void An_unmeasurable_volume_does_not_block_the_build()
    {
        var environment = new FakeBuildEnvironment { DefaultFreeBytes = long.MaxValue };
        DiskSpace.Require(environment, @"\\server\share\scratch", 500 * OneGb, "Anything");
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(8_589_934_592, "8 GB")]
    public void Byte_sizes_read_the_way_a_person_would_say_them(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    /// <summary>Free space that changes between calls, to model a volume filling mid-build.</summary>
    private sealed class ShrinkingEnvironment(Func<long> next) : IBuildEnvironment
    {
        public bool IsElevated => true;

        public long GetAvailableFreeBytes(string path) => next();
    }
}
