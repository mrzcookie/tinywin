using System.Diagnostics;

namespace TinyWin.Core.Pipeline;

/// <summary>
/// Runs the stages in order, tracks progress, and guarantees cleanup.
/// </summary>
/// <remarks>
/// The rollback behaviour is the point of this class. A build that fails or is cancelled partway
/// must not leave a mounted image or a loaded hive behind, because either strands the machine
/// until reboot. Stages that started are rolled back in reverse order, and a rollback that itself
/// throws is recorded but never allowed to mask the original failure.
/// </remarks>
public sealed class BuildPipeline(IReadOnlyList<IBuildStage> stages)
{
    private readonly IReadOnlyList<IBuildStage> _stages = stages;

    public async Task<BuildReport> RunAsync(
        BuildContext context,
        IProgress<BuildProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var reports = new List<StageReport>();
        var started = new Stack<IBuildStage>();
        var total = Stopwatch.StartNew();
        var failed = false;

        foreach (var stage in _stages)
        {
            if (!stage.ShouldRun(context))
            {
                reports.Add(new StageReport { Stage = stage.Id, State = StageState.Skipped });
                progress.Report(new BuildProgress
                {
                    Stage = stage.Id,
                    State = StageState.Skipped,
                    Message = $"{stage.Title} — not required for this selection",
                    Elapsed = total.Elapsed,
                });
                continue;
            }

            var sw = Stopwatch.StartNew();
            progress.Report(new BuildProgress
            {
                Stage = stage.Id,
                State = StageState.Running,
                Message = stage.Title,
                Elapsed = total.Elapsed,
            });

            try
            {
                started.Push(stage);
                await stage.ExecuteAsync(context, progress, cancellationToken).ConfigureAwait(false);

                reports.Add(new StageReport
                {
                    Stage = stage.Id,
                    State = StageState.Completed,
                    Duration = sw.Elapsed,
                });
            }
            catch (Exception ex)
            {
                failed = true;
                reports.Add(new StageReport
                {
                    Stage = stage.Id,
                    State = StageState.Failed,
                    Duration = sw.Elapsed,
                    Error = ex.Message,
                });

                progress.Report(new BuildProgress
                {
                    Stage = stage.Id,
                    State = StageState.Failed,
                    Message = $"{stage.Title} failed: {ex.Message}",
                    Elapsed = total.Elapsed,
                });

                // Cancellation is a normal outcome, not an error worth re-throwing to the UI, but
                // it still has to unwind the mount exactly like a failure does.
                await RollbackAsync(started, context, progress).ConfigureAwait(false);
                break;
            }
        }

        if (!failed)
        {
            await RollbackNonDestructiveAsync(started, context).ConfigureAwait(false);
        }

        return new BuildReport
        {
            Succeeded = !failed,
            Stages = reports,
            Actions = context.Outcomes,
            Warnings = context.Warnings,
            TotalDuration = total.Elapsed,
            OutputIsoPath = failed ? null : context.Request.OutputIsoPath,
        };
    }

    private static async Task RollbackAsync(
        Stack<IBuildStage> started, BuildContext context, IProgress<BuildProgress> progress)
    {
        while (started.Count > 0)
        {
            var stage = started.Pop();
            try
            {
                await stage.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never let cleanup failure hide the original error, but never swallow it either —
                // a failed dismount is exactly what the user needs to know about.
                context.Warn($"Rollback of '{stage.Title}' failed: {ex.Message}");
                progress.Report(new BuildProgress
                {
                    Stage = stage.Id,
                    State = StageState.Failed,
                    Message = $"Cleanup after {stage.Title} failed: {ex.Message}",
                });
            }
        }
    }

    /// <summary>
    /// On success, stages still get a chance to release resources they hold open — but any stage
    /// whose rollback would undo committed work must be a no-op once it has completed.
    /// </summary>
    private static async Task RollbackNonDestructiveAsync(Stack<IBuildStage> started, BuildContext context)
    {
        while (started.Count > 0)
        {
            var stage = started.Pop();
            try
            {
                await stage.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Warn($"Cleanup of '{stage.Title}' failed: {ex.Message}");
            }
        }
    }
}
