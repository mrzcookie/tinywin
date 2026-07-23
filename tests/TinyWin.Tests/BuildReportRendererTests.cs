using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;
using TinyWin.Core.Reporting;

namespace TinyWin.Tests;

/// <summary>
/// The end-of-build summary.
/// </summary>
/// <remarks>
/// The no-target block is the part with a requirement behind it rather than a preference:
/// docs/PLAN.md section 2.1 makes a high no-op count the signal that the catalog has drifted from
/// the media, and the counts existed on <see cref="BuildReport"/> for a while with nothing
/// rendering them. A signal nobody prints is not a signal.
/// </remarks>
public sealed class BuildReportRendererTests
{
    private static ActionOutcome Outcome(string component, ActionStatus status, string description) =>
        new() { ComponentId = component, Description = description, Status = status };

    private static BuildReport Report(
        IReadOnlyList<ActionOutcome>? actions = null,
        bool succeeded = true,
        IReadOnlyList<StageReport>? stages = null,
        IReadOnlyList<string>? warnings = null) => new()
        {
            Succeeded = succeeded,
            Stages = stages ??
            [
                new StageReport
                {
                    Stage = BuildStageId.StageFiles,
                    State = StageState.Completed,
                    Duration = TimeSpan.FromMinutes(3.5),
                },
                new StageReport { Stage = BuildStageId.CleanupImage, State = StageState.Skipped },
            ],
            Actions = actions ?? [],
            Warnings = warnings ?? [],
            SourceSizeBytes = 8_471_603_200,
            OutputSizeBytes = 4_235_801_600,
            TotalDuration = TimeSpan.FromMinutes(42),
            OutputIsoPath = @"D:\ISOs\Win11-tiny.iso",
        };

    private static string Render(BuildReport report) =>
        string.Join(System.Environment.NewLine, BuildReportRenderer.Render(report));

    [Fact]
    public void The_headline_carries_size_before_and_after_and_the_saving()
    {
        var text = Render(Report());

        Assert.Contains("BUILD SUCCEEDED", text, StringComparison.Ordinal);
        Assert.Contains("7.89 GB -> 3.94 GB", text, StringComparison.Ordinal);
        Assert.Contains("(-50%)", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\ISOs\Win11-tiny.iso", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Per_stage_timings_and_skips_are_both_shown()
    {
        var text = Render(Report());

        Assert.Contains("StageFiles", text, StringComparison.Ordinal);
        Assert.Contains("3:30", text, StringComparison.Ordinal);
        Assert.Contains("skip", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reused_stage_is_labelled_differently_from_a_skipped_one()
    {
        var text = Render(Report(stages:
        [
            new StageReport { Stage = BuildStageId.StageFiles, State = StageState.Skipped, Restored = true },
            new StageReport { Stage = BuildStageId.CleanupImage, State = StageState.Skipped },
        ]));

        Assert.Contains("reused", text, StringComparison.Ordinal);
        Assert.Contains("skip", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_no_target_block_names_the_components_responsible_and_their_counts()
    {
        var text = Render(Report(
        [
            Outcome("apps.xbox", ActionStatus.Applied, "Remove app package Microsoft.XboxApp"),
            Outcome("apps.xbox", ActionStatus.NoTarget, "Remove app package Microsoft.XboxGameOverlay"),
            Outcome("apps.xbox", ActionStatus.NoTarget, "Remove app package Microsoft.Xbox.TCUI"),
            Outcome("privacy.telemetry", ActionStatus.NoTarget, "Remove scheduled task PcaPatchDbTask"),
        ]));

        Assert.Contains("3 of 4 actions found no target", text, StringComparison.Ordinal);
        Assert.Contains("apps.xbox", text, StringComparison.Ordinal);
        Assert.Contains("privacy.telemetry", text, StringComparison.Ordinal);
        Assert.Contains("Microsoft.XboxGameOverlay", text, StringComparison.Ordinal);
        Assert.Contains("drifted", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A couple of no-ops in a large build is normal and must not read like an alarm, or the alarm
    /// stops being informative when it matters.
    /// </summary>
    [Fact]
    public void A_low_no_target_share_is_reported_without_the_drift_warning()
    {
        var actions = new List<ActionOutcome>();
        for (var i = 0; i < 99; i++)
        {
            actions.Add(Outcome("apps.xbox", ActionStatus.Applied, $"Remove app package Package{i}"));
        }

        actions.Add(Outcome("apps.xbox", ActionStatus.NoTarget, "Remove app package Missing"));

        var text = Render(Report(actions));

        Assert.Contains("1 of 100 actions found no target", text, StringComparison.Ordinal);
        Assert.DoesNotContain("drifted", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_run_has_no_no_target_block_at_all()
    {
        var text = Render(Report([Outcome("apps.xbox", ActionStatus.Applied, "Remove app package X")]));

        Assert.DoesNotContain("found no target", text, StringComparison.Ordinal);
        Assert.Contains("applied 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_renders_the_error_the_advice_and_the_way_to_continue()
    {
        var text = Render(Report(
            succeeded: false,
            stages:
            [
                new StageReport
                {
                    Stage = BuildStageId.MountImage,
                    State = StageState.Failed,
                    Error = "Error: 740",
                    Advice = "Run TinyWin as Administrator.",
                },
            ]));

        Assert.Contains("BUILD FAILED", text, StringComparison.Ordinal);
        Assert.Contains("Error: 740", text, StringComparison.Ordinal);
        Assert.Contains("Next step: Run TinyWin as Administrator.", text, StringComparison.Ordinal);
        Assert.Contains("--resume", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_actions_are_listed_with_their_detail()
    {
        var text = Render(Report(
        [
            new ActionOutcome
            {
                ComponentId = "system.defender",
                Description = "Remove package Windows-Defender",
                Status = ActionStatus.Failed,
                Detail = "Access is denied.",
            },
        ]));

        Assert.Contains("Failed actions (1)", text, StringComparison.Ordinal);
        Assert.Contains("system.defender", text, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Warnings_are_listed_rather_than_dropped()
    {
        var text = Render(Report(warnings: ["Unloaded 1 registry hive(s) stranded by a previous run."]));

        Assert.Contains("Warnings (1)", text, StringComparison.Ordinal);
        Assert.Contains("stranded", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resumed_run_says_so_and_says_from_where()
    {
        var report = Report() with
        {
            ResumedFrom = BuildStageId.BuildIso,
            RestoredStages = [BuildStageId.StageFiles, BuildStageId.MountImage],
        };

        var text = Render(report);

        Assert.Contains("resumed", text, StringComparison.Ordinal);
        Assert.Contains("BuildIso", text, StringComparison.Ordinal);
        Assert.Contains("2 completed stage(s)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_run_is_not_reported_as_a_failure()
    {
        var text = Render(Report(succeeded: false) with { Cancelled = true });

        Assert.Contains("BUILD CANCELLED", text, StringComparison.Ordinal);
    }
}
