namespace TinyWin.Imaging.Dism;

/// <summary>
/// Adapts what DISM actually tells us about progress onto <see cref="IProgress{T}"/> of
/// <see cref="double"/>.
/// </summary>
/// <remarks>
/// <para>The spike could not determine whether DISM draws its progress bar at all when stdout is a
/// pipe rather than a console (docs/spikes/dism-backend.md §5). So this type is built for the case
/// where it does not: an operation that reports nothing must not look identical to an operation
/// stuck at 0%.</para>
///
/// <para>Three behaviours, in order of preference:</para>
/// <list type="number">
///   <item><b>Real percentages.</b> Forwarded verbatim. Truth always wins.</item>
///   <item><b>Stage transitions.</b> When no percentage has been seen, each new line DISM prints
///     nudges progress along a short ladder that tops out at <see cref="CoarseCeiling"/>. This is
///     movement, not measurement — it says "DISM is alive and has reached a new step", and it is
///     capped low precisely so it cannot masquerade as a real reading or overshoot a real one that
///     arrives later.</item>
///   <item><b>Nothing.</b> 0.0 at the start and 1.0 on success are always reported, so even a
///     completely silent operation has a beginning and an end.</item>
/// </list>
/// </remarks>
public sealed class DismStageProgress
{
    /// <summary>
    /// The most a stage transition alone will claim. Deliberately small: DISM emitting three lines
    /// tells us nothing about how much of a 40-minute <c>/ResetBase</c> is done, and a coarse value
    /// that outran a subsequent real percentage would make the bar jump backwards.
    /// </summary>
    public const double CoarseCeiling = 0.15;

    private const double CoarseStep = 0.03;

    private readonly IProgress<double>? _target;
    private int _stages;

    public DismStageProgress(IProgress<double>? target) => _target = target;

    /// <summary>True once DISM has emitted a real percentage.</summary>
    public bool SawPercentage { get; private set; }

    public void Start() => _target?.Report(0.0);

    public void ReportPercentage(double fraction)
    {
        SawPercentage = true;
        _target?.Report(Math.Clamp(fraction, 0.0, 1.0));
    }

    /// <summary>Called for each line DISM prints. Only advances progress in the no-percentage case.</summary>
    public void ReportStage()
    {
        if (SawPercentage)
        {
            return;
        }

        _stages++;
        _target?.Report(Math.Min(CoarseCeiling, _stages * CoarseStep));
    }

    public void Complete() => _target?.Report(1.0);
}
