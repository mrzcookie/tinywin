using TinyWin.Imaging.Dism;

namespace TinyWin.Imaging.Tests;

/// <summary>
/// The fallback for the case the spike could not rule out: DISM emitting no percentages at all
/// under stdout redirection (docs/spikes/dism-backend.md §5).
/// </summary>
public class DismStageProgressTests
{
    private readonly List<double> _reported = [];

    private DismStageProgress NewProgress() => new(new Sink(_reported.Add));

    [Fact]
    public void Real_percentages_are_forwarded_unchanged()
    {
        var progress = NewProgress();

        progress.Start();
        progress.ReportPercentage(0.25);
        progress.ReportPercentage(0.75);
        progress.Complete();

        Assert.Equal([0.0, 0.25, 0.75, 1.0], _reported);
        Assert.True(progress.SawPercentage);
    }

    /// <summary>Without this, a silent 40-minute operation is indistinguishable from a hung one.</summary>
    [Fact]
    public void With_no_percentages_stage_transitions_move_progress_off_zero()
    {
        var progress = NewProgress();

        progress.Start();
        progress.ReportStage();
        progress.ReportStage();

        Assert.False(progress.SawPercentage);
        Assert.Equal(0.0, _reported[0]);
        Assert.True(_reported[^1] > 0.0, "Stage transitions must move progress off zero.");
    }

    /// <summary>
    /// Stage transitions are movement, not measurement. Letting them climb would put a made-up
    /// number in front of the user and make a real percentage arriving later look like a regression.
    /// </summary>
    [Fact]
    public void Stage_transitions_never_exceed_the_coarse_ceiling()
    {
        var progress = NewProgress();

        progress.Start();
        for (var i = 0; i < 500; i++)
        {
            progress.ReportStage();
        }

        Assert.All(_reported, value => Assert.True(value <= DismStageProgress.CoarseCeiling));
    }

    [Fact]
    public void Once_a_real_percentage_arrives_stage_transitions_stop_contributing()
    {
        var progress = NewProgress();

        progress.Start();
        progress.ReportStage();
        progress.ReportPercentage(0.5);
        var countAfterPercentage = _reported.Count;
        progress.ReportStage();
        progress.ReportStage();

        Assert.Equal(countAfterPercentage, _reported.Count);
    }

    [Fact]
    public void Progress_always_ends_at_one()
    {
        var progress = NewProgress();

        progress.Start();
        progress.Complete();

        Assert.Equal([0.0, 1.0], _reported);
    }

    [Fact]
    public void A_null_target_is_harmless()
    {
        var progress = new DismStageProgress(null);

        progress.Start();
        progress.ReportStage();
        progress.ReportPercentage(0.5);
        progress.Complete();
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(2.0, 1.0)]
    public void Out_of_range_percentages_are_clamped(double input, double expected)
    {
        var progress = NewProgress();

        progress.ReportPercentage(input);

        Assert.Equal(expected, _reported[0]);
    }

    private sealed class Sink(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
