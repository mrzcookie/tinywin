using TinyWin.Catalog.Resolution;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Core.Recovery;

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

    /// <summary>
    /// El Torito values read off the source ISO during Inspect, so the rebuilt image reproduces
    /// them rather than guessing. See docs/spikes/iso-build.md section 7.
    /// </summary>
    public IsoBootGeometry? BootGeometry { get; set; }

    /// <summary>
    /// Set when a stage rewrites the image such that the requested index no longer applies —
    /// exporting an ESD produces a WIM holding only the selected edition, which is then index 1.
    /// </summary>
    public int? EditionIndexOverride { get; set; }

    /// <summary>The index later stages must actually use. Always prefer this over the request.</summary>
    public int EditionIndex => EditionIndexOverride ?? Request.EditionIndex;

    private readonly List<ActionOutcome> _outcomes = [];
    private readonly List<string> _warnings = [];

    public IReadOnlyList<ActionOutcome> Outcomes => _outcomes;
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// The stage currently executing, set by <see cref="BuildPipeline"/>.
    /// </summary>
    /// <remarks>
    /// Outcomes are stamped with it so a resumed run can keep the results of the stages it skips
    /// and discard the results of the ones it redoes. Null when a stage is driven directly, as in
    /// a unit test.
    /// </remarks>
    public BuildStageId? CurrentStage { get; set; }

    public void Record(ActionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _outcomes.Add(outcome.Stage is null && CurrentStage is not null
            ? outcome with { Stage = CurrentStage }
            : outcome);
    }

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
    /// Whether this stage's completed work survives into a later run, and can therefore be skipped
    /// on resume. Defaults to <see cref="StageRecovery.Durable"/>; anything writing into a mounted
    /// image must say <see cref="StageRecovery.Volatile"/> instead.
    /// </summary>
    StageRecovery Recovery => StageRecovery.Durable;

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
