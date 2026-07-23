using System.Diagnostics;
using TinyWin.Core.Diagnostics;
using TinyWin.Core.Recovery;

namespace TinyWin.Core.Pipeline;

/// <summary>
/// Runs the stages in order, tracks progress, checkpoints, and guarantees cleanup.
/// </summary>
/// <remarks>
/// <para>
/// The rollback behaviour is the point of this class. A build that fails or is cancelled partway
/// must not leave a mounted image or a loaded hive behind, because either strands the machine
/// until reboot. Stages that started are rolled back in reverse order, and a rollback that itself
/// throws is recorded but never allowed to mask the original failure.
/// </para>
/// <para>
/// Checkpointing is the other half of the same idea. Rollback protects the machine; the
/// checkpoint protects the user's time — a run that dies in stage 11 should not re-copy 6 GB to
/// try again. A checkpoint is written after every stage, and deleted once the build succeeds so
/// nothing stale is resumable. See docs/PLAN.md section 2.2 and
/// <see cref="Recovery.StageRecovery"/> for why "completed" alone is not enough to skip a stage.
/// </para>
/// </remarks>
public sealed class BuildPipeline(IReadOnlyList<IBuildStage> stages, ICheckpointStore? checkpoints = null)
{
    private readonly IReadOnlyList<IBuildStage> _stages = stages;

    /// <summary>Wall-clock source for checkpoint timestamps. Overridable so tests are deterministic.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public async Task<BuildReport> RunAsync(
        BuildContext context,
        IProgress<BuildProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var reports = new List<StageReport>();
        var started = new Stack<IBuildStage>();
        var completed = new List<BuildStageId>();
        var total = Stopwatch.StartNew();
        var failed = false;
        var cancelled = false;

        var resume = await PrepareResumeAsync(context, completed, cancellationToken).ConfigureAwait(false);

        foreach (var stage in _stages)
        {
            if (resume.Restored.Contains(stage.Id))
            {
                reports.Add(new StageReport
                {
                    Stage = stage.Id,
                    State = StageState.Skipped,
                    Restored = true,
                });

                progress.Report(new BuildProgress
                {
                    Stage = stage.Id,
                    State = StageState.Skipped,
                    Message = $"{stage.Title} — already done in the interrupted run, reusing it",
                    Elapsed = total.Elapsed,
                });

                continue;
            }

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

                // A stage with nothing to do is done, and a resumed run must not reconsider it:
                // NormalizeImage deletes the ESD it was asked about, so re-deciding on stale
                // context would send it looking for a file it already consumed.
                completed.Add(stage.Id);
                await SaveCheckpointAsync(context, completed).ConfigureAwait(false);
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
                context.CurrentStage = stage.Id;
                await stage.ExecuteAsync(context, progress, cancellationToken).ConfigureAwait(false);

                reports.Add(new StageReport
                {
                    Stage = stage.Id,
                    State = StageState.Completed,
                    Duration = sw.Elapsed,
                });

                completed.Add(stage.Id);
                await SaveCheckpointAsync(context, completed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failed = true;
                cancelled = ex is OperationCanceledException;

                var advice = FailureAdvice.For(ex);
                reports.Add(new StageReport
                {
                    Stage = stage.Id,
                    State = StageState.Failed,
                    Duration = sw.Elapsed,
                    Error = ex.Message,
                    Advice = advice,
                });

                progress.Report(new BuildProgress
                {
                    Stage = stage.Id,
                    State = StageState.Failed,
                    Message = cancelled
                        ? $"{stage.Title} cancelled — rolling back"
                        : $"{stage.Title} failed: {ex.Message}",
                    Elapsed = total.Elapsed,
                });

                // Cancellation is a normal outcome, not an error worth re-throwing to the UI, but
                // it still has to unwind the mount exactly like a failure does.
                await RollbackAsync(started, context, progress).ConfigureAwait(false);
                break;
            }
            finally
            {
                context.CurrentStage = null;
            }
        }

        if (failed)
        {
            // Written after the rollback, not before: the resume logic reads it back knowing the
            // mount was discarded, and re-runs everything that lived inside it.
            await SaveCheckpointAsync(context, completed).ConfigureAwait(false);
        }
        else
        {
            await RollbackNonDestructiveAsync(started, context).ConfigureAwait(false);

            // The build is done, so the checkpoint is now a trap: resuming it would skip every
            // stage and report a success that redid nothing.
            if (checkpoints is not null)
            {
                await checkpoints.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        return new BuildReport
        {
            Succeeded = !failed,
            Cancelled = cancelled,
            Stages = reports,
            Actions = context.Outcomes,
            Warnings = context.Warnings,
            TotalDuration = total.Elapsed,
            OutputIsoPath = failed ? null : context.Request.OutputIsoPath,
            SourceSizeBytes = SizeOf(context.Request.SourceIsoPath),
            OutputSizeBytes = failed ? 0 : SizeOf(context.Request.OutputIsoPath),
            ResumedFrom = resume.ResumeFrom,
            RestoredStages = [.. resume.Restored],
        };
    }

    /// <summary>
    /// Loads the checkpoint when the request asked to resume, and rebuilds the context from it.
    /// </summary>
    private async Task<ResumePlan> PrepareResumeAsync(
        BuildContext context, List<BuildStageId> completed, CancellationToken cancellationToken)
    {
        if (!context.Request.Resume || checkpoints is null)
        {
            return ResumePlan.None;
        }

        var checkpoint = await checkpoints.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new BuildCheckpointException(
                $"No usable checkpoint was found in '{context.Request.ScratchDirectory}', so there is " +
                "nothing to resume. Run the same command without --resume to start a fresh build.");

        if (!string.Equals(checkpoint.Fingerprint, BuildCheckpoint.FingerprintOf(context.Request), StringComparison.Ordinal))
        {
            throw new BuildCheckpointException(
                $"The checkpoint in '{context.Request.ScratchDirectory}' belongs to a different build " +
                $"(source '{checkpoint.SourceIsoPath}', saved {checkpoint.UpdatedUtc:u}). Resuming it would " +
                "mix two images. Run without --resume to start over, or point --scratch at a different " +
                "directory.");
        }

        var plan = ResumePlan.Compute(
            [.. _stages.Select(s => new StageRecoveryInfo(s.Id, s.Recovery))],
            checkpoint.CompletedStages);

        checkpoint.RestoreInto(context, plan.Restored);
        completed.AddRange(plan.Restored);

        return plan;
    }

    private async Task SaveCheckpointAsync(BuildContext context, IReadOnlyCollection<BuildStageId> completed)
    {
        if (checkpoints is null)
        {
            return;
        }

        try
        {
            var checkpoint = BuildCheckpoint.From(context, completed, TimeProvider.GetUtcNow());
            await checkpoints.SaveAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing resumability is not worth losing the build over — but it is worth saying so,
            // because the user will otherwise discover it only when --resume refuses to run.
            context.Warn($"Could not write the resume checkpoint: {ex.Message}");
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
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
                var advice = FailureAdvice.For(ex);
                context.Warn($"Rollback of '{stage.Title}' failed: {ex.Message}" +
                    (advice is null ? string.Empty : $" {advice}"));

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
