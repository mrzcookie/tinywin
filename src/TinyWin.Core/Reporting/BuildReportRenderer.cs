using System.Globalization;
using TinyWin.Core.Diagnostics;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;

namespace TinyWin.Core.Reporting;

/// <summary>
/// Renders a <see cref="BuildReport"/> as plain text lines.
/// </summary>
/// <remarks>
/// <para>
/// In Core rather than in the CLI so both heads say the same thing, and so it is testable without
/// a console. It returns lines rather than writing them, which is the only reason the no-op
/// section below can be asserted on.
/// </para>
/// <para>
/// The no-target section is the part that matters. docs/PLAN.md section 2.1 calls a high no-op
/// count the signal that the catalog has drifted from the media, and a signal nobody renders is
/// not a signal — so it gets its own block, names the components responsible, and states the
/// share rather than only the count. "12 no-ops" reads very differently in a 20-action minimal
/// build than in a 340-action core build.
/// </para>
/// </remarks>
public static class BuildReportRenderer
{
    /// <summary>Above this share of actions finding nothing, the catalog is called out as suspect.</summary>
    public const double DriftWarningRatio = 0.10;

    public static IReadOnlyList<string> Render(BuildReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>();

        RenderHeadline(report, lines);
        RenderStages(report, lines);
        RenderActions(report, lines);
        RenderNoTargets(report, lines);
        RenderFailures(report, lines);
        RenderWarnings(report, lines);
        RenderNextStep(report, lines);

        return lines;
    }

    private static void RenderHeadline(BuildReport report, List<string> lines)
    {
        var headline = report switch
        {
            { Succeeded: true } => "BUILD SUCCEEDED",
            { Cancelled: true } => "BUILD CANCELLED",
            _ => "BUILD FAILED",
        };

        lines.Add(headline);
        lines.Add($"  elapsed        {Duration(report.TotalDuration)}");

        if (report.ResumedFrom is { } resumed)
        {
            lines.Add($"  resumed        from {resumed}, reusing {report.RestoredStages.Count} completed stage(s)");
        }

        if (report.SourceSizeBytes > 0 && report.OutputSizeBytes > 0)
        {
            var delta = report.SourceSizeBytes - report.OutputSizeBytes;
            var percent = (double)delta / report.SourceSizeBytes * 100;
            lines.Add(
                $"  size           {ByteSize.Format(report.SourceSizeBytes)} -> " +
                $"{ByteSize.Format(report.OutputSizeBytes)}  " +
                $"({(delta >= 0 ? "-" : "+")}{Math.Abs(percent).ToString("0.#", CultureInfo.InvariantCulture)}%)");
        }
        else if (report.SourceSizeBytes > 0)
        {
            lines.Add($"  source size    {ByteSize.Format(report.SourceSizeBytes)}");
        }

        if (report.OutputIsoPath is { } output && report.Succeeded)
        {
            lines.Add($"  output         {output}");
        }

        lines.Add(string.Empty);
    }

    private static void RenderStages(BuildReport report, List<string> lines)
    {
        if (report.Stages.Count == 0)
        {
            return;
        }

        lines.Add("Stages");

        var width = report.Stages.Max(s => s.Stage.ToString().Length);

        foreach (var stage in report.Stages)
        {
            var mark = stage switch
            {
                { State: StageState.Completed } => "ok    ",
                { State: StageState.Skipped, Restored: true } => "reused",
                { State: StageState.Skipped } => "skip  ",
                { State: StageState.Failed } => "FAILED",
                _ => "?     ",
            };

            var duration = stage.State == StageState.Completed ? Duration(stage.Duration) : string.Empty;
            lines.Add($"  {mark}  {stage.Stage.ToString().PadRight(width)}  {duration}".TrimEnd());

            if (stage.Error is { } error)
            {
                lines.Add($"          {error}");
            }

            if (stage.Advice is { } advice)
            {
                lines.Add($"          -> {advice}");
            }
        }

        lines.Add(string.Empty);
    }

    private static void RenderActions(BuildReport report, List<string> lines)
    {
        lines.Add("Actions");
        lines.Add(
            $"  applied {report.AppliedCount}   no target {report.NoTargetCount}   " +
            $"failed {report.FailedCount}   deferred {report.SkippedCount}");
        lines.Add(string.Empty);
    }

    private static void RenderNoTargets(BuildReport report, List<string> lines)
    {
        if (report.NoTargetCount == 0)
        {
            return;
        }

        var share = (report.NoTargetRatio * 100).ToString("0.#", CultureInfo.InvariantCulture);
        lines.Add(
            $"!! {report.NoTargetCount} of {report.Actions.Count} actions found no target ({share}%).");

        lines.Add(report.NoTargetRatio >= DriftWarningRatio
            ? "   That is high enough to suspect the catalog has drifted from this media rather than " +
              "the components simply being absent. Verify these component ids against the build before " +
              "trusting the result — see docs/PLAN.md section 2.1."
            : "   Some of these are normal — an edition may genuinely not ship a component. Worth a " +
              "glance if the same ids keep appearing across builds.");

        lines.Add(string.Empty);

        foreach (var group in report.Actions
            .Where(a => a.Status == ActionStatus.NoTarget)
            .GroupBy(a => a.ComponentId)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            lines.Add($"   {group.Key,-32} {group.Count(),3}  {Sample(group)}");
        }

        lines.Add(string.Empty);
    }

    private static void RenderFailures(BuildReport report, List<string> lines)
    {
        if (report.FailedCount == 0)
        {
            return;
        }

        lines.Add($"Failed actions ({report.FailedCount})");
        foreach (var failure in report.Actions.Where(a => a.Status == ActionStatus.Failed))
        {
            lines.Add($"   [{failure.ComponentId}] {failure.Description}");
            if (failure.Detail is { } detail)
            {
                lines.Add($"       {detail}");
            }
        }

        lines.Add(string.Empty);
    }

    private static void RenderWarnings(BuildReport report, List<string> lines)
    {
        if (report.Warnings.Count == 0)
        {
            return;
        }

        lines.Add($"Warnings ({report.Warnings.Count})");
        foreach (var warning in report.Warnings)
        {
            lines.Add($"   {warning}");
        }

        lines.Add(string.Empty);
    }

    /// <summary>The one line a user needs after a failure: what to do next.</summary>
    private static void RenderNextStep(BuildReport report, List<string> lines)
    {
        if (report.Succeeded)
        {
            return;
        }

        var failure = report.FailedStage;
        if (failure?.Advice is { } advice)
        {
            lines.Add($"Next step: {advice}");
        }

        lines.Add(
            "The staged files were kept. Re-run the same command with --resume to continue from the " +
            "last completed stage instead of copying the ISO again.");
    }

    private static string Sample(IEnumerable<ActionOutcome> outcomes)
    {
        var first = outcomes.Select(o => o.Description).Take(2).ToList();
        return string.Join("; ", first);
    }

    private static string Duration(TimeSpan value) =>
        value >= TimeSpan.FromHours(1)
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
