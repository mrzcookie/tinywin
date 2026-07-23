using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;

namespace TinyWin.Core.Recovery;

/// <summary>
/// The <c>state.json</c> of docs/PLAN.md section 2.2 — enough to rebuild a
/// <see cref="BuildContext"/> and carry on where the last run stopped.
/// </summary>
/// <remarks>
/// <para>
/// What is deliberately <em>not</em> here is the mounted image. A mount does outlive the process,
/// but a run that failed or was cancelled dismounted it with <c>/Discard</c>, and one that died
/// hard left a mount that preflight cleans up before anything else runs. Persisting it would offer
/// a resumed build a handle to something that is either gone or about to be — see
/// <see cref="StageRecovery.Volatile"/>.
/// </para>
/// <para>
/// Outcomes are stamped with the stage that produced them so a resumed run can keep the ones whose
/// stage it is skipping and drop the ones it is about to redo. Without that the report would
/// double-count every action across a resume, and the no-op count — the number docs/PLAN.md
/// section 2.1 asks us to treat as the health signal for the catalog — would be fiction.
/// </para>
/// </remarks>
public sealed record BuildCheckpoint
{
    /// <summary>Bumped when the shape changes. A checkpoint from an older schema is not resumed.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Identifies the build this checkpoint belongs to. See <see cref="FingerprintOf"/>.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>For the "resume this 3-hour-old build?" question a UI will want to ask.</summary>
    public required DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>Human-readable, so a stranded state.json can be understood without the app.</summary>
    public string? SourceIsoPath { get; init; }

    public required IReadOnlyList<BuildStageId> CompletedStages { get; init; }

    public string? StagedIsoDirectory { get; init; }

    public string? InstallWimPath { get; init; }

    public int? EditionIndexOverride { get; init; }

    public WindowsImageInfo? ImageInfo { get; init; }

    public IsoBootGeometry? BootGeometry { get; init; }

    public IReadOnlyList<ActionOutcome> Outcomes { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Captures the current state of <paramref name="context"/>.</summary>
    public static BuildCheckpoint From(
        BuildContext context, IReadOnlyCollection<BuildStageId> completedStages, DateTimeOffset updatedUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completedStages);

        return new BuildCheckpoint
        {
            Fingerprint = FingerprintOf(context.Request),
            UpdatedUtc = updatedUtc,
            SourceIsoPath = context.Request.SourceIsoPath,
            CompletedStages = [.. completedStages],
            StagedIsoDirectory = context.StagedIsoDirectory,
            InstallWimPath = context.InstallWimPath,
            EditionIndexOverride = context.EditionIndexOverride,
            ImageInfo = context.ImageInfo,
            BootGeometry = context.BootGeometry,
            Outcomes = [.. context.Outcomes],
            Warnings = [.. context.Warnings],
        };
    }

    /// <summary>
    /// Restores the parts of <paramref name="context"/> that the skipped stages would have set.
    /// </summary>
    /// <param name="context">The context to fill in.</param>
    /// <param name="restoredStages">Stages whose work is being reused.</param>
    public void RestoreInto(BuildContext context, IReadOnlySet<BuildStageId> restoredStages)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(restoredStages);

        context.StagedIsoDirectory = StagedIsoDirectory;
        context.InstallWimPath = InstallWimPath;
        context.EditionIndexOverride = EditionIndexOverride;
        context.ImageInfo = ImageInfo;
        context.BootGeometry = BootGeometry;

        // Only the outcomes belonging to stages we are not going to run again. The rest are about
        // to be produced afresh.
        foreach (var outcome in Outcomes.Where(o => o.Stage is null || restoredStages.Contains(o.Stage.Value)))
        {
            context.Record(outcome);
        }

        foreach (var warning in Warnings)
        {
            context.Warn(warning);
        }
    }

    /// <summary>
    /// Identifies a build by everything that would make a checkpoint the wrong one to resume.
    /// </summary>
    /// <remarks>
    /// The source ISO's length is folded in as well as its path, because "same path, different
    /// ISO" is exactly how someone ends up resuming a 25H2 build onto 24H2 media and getting an
    /// image that is a blend of the two.
    /// </remarks>
    public static string FingerprintOf(BuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder();
        builder.Append(Path.GetFullPath(request.SourceIsoPath)).Append('\n');
        builder.Append(SourceLengthOf(request.SourceIsoPath)).Append('\n');
        builder.Append(Path.GetFullPath(request.OutputIsoPath)).Append('\n');
        builder.Append(request.EditionIndex).Append('\n');
        builder.Append(request.RecompressImage).Append('\n');
        builder.Append(request.CleanupComponentStore).Append('\n');

        foreach (var id in request.ComponentIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(id).Append(',');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static string SourceLengthOf(string path)
    {
        try
        {
            return File.Exists(path)
                ? new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture)
                : "missing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }
}

/// <summary>
/// A checkpoint exists but cannot be used for this build.
/// </summary>
/// <remarks>
/// Thrown rather than silently ignored. A resumed build that quietly starts over re-copies 6 GB
/// while the user watches a progress bar they expected to see skipped, and a resumed build that
/// quietly reuses the wrong checkpoint produces mixed media. Both deserve a sentence.
/// </remarks>
public sealed class BuildCheckpointException : Exception
{
    public BuildCheckpointException()
    {
    }

    public BuildCheckpointException(string message)
        : base(message)
    {
    }

    public BuildCheckpointException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
