using TinyWin.Catalog.Resolution;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;

namespace TinyWin.Core.Pipeline;

/// <summary>
/// Mutable state threaded through the stages. Stages communicate by filling this in rather than by
/// returning values, so a resumed run can rebuild context from a checkpoint.
/// </summary>
public sealed class BuildContext
{
    public required BuildRequest Request { get; init; }
    public required ResolvedPlan Plan { get; init; }

    /// <summary>Where the ISO tree was staged. Set by <see cref="BuildStageId.StageFiles"/>.</summary>
    public string? StagedIsoDirectory { get; set; }

    public string? InstallWimPath { get; set; }
    public MountedImage? MountedImage { get; set; }
    public WindowsImageInfo? ImageInfo { get; set; }

    private readonly List<ActionOutcome> _outcomes = [];
    private readonly List<string> _warnings = [];

    public IReadOnlyList<ActionOutcome> Outcomes => _outcomes;
    public IReadOnlyList<string> Warnings => _warnings;

    public void Record(ActionOutcome outcome) => _outcomes.Add(outcome);

    public void Warn(string message) => _warnings.Add(message);
}

/// <summary>
/// One stage of the build. Stages must be individually resumable: given a context rebuilt from a
/// checkpoint, running a stage again must either be a no-op or safely redo its work.
/// </summary>
public interface IBuildStage
{
    BuildStageId Id { get; }

    string Title { get; }

    /// <summary>
    /// Whether this stage applies to the current request. Skipping is reported, never silent —
    /// for example component cleanup is skipped when the store has already been removed.
    /// </summary>
    bool ShouldRun(BuildContext context) => true;

    Task ExecuteAsync(
        BuildContext context,
        IProgress<BuildProgress> progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Undo whatever must not be left behind if the build fails or is cancelled after this stage
    /// started — most importantly dismounting an image with discard. Must be safe to call when the
    /// stage never ran, and must not throw.
    /// </summary>
    Task RollbackAsync(BuildContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
