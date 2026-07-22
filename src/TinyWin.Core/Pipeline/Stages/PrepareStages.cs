using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;

namespace TinyWin.Core.Pipeline.Stages;

/// <summary>
/// Reads the source ISO: editions, build number, and the El Torito geometry the rebuilt image
/// must reproduce.
/// </summary>
/// <remarks>
/// Capturing boot geometry here rather than at build time is deliberate. xorriso silently accepts
/// a wrong <c>-boot-load-size</c>, so a guessed value produces media that fails only at boot —
/// see docs/spikes/iso-build.md section 5.
/// </remarks>
public sealed class InspectIsoStage(IImagingBackend backend, IIsoBuilder isoBuilder) : IBuildStage
{
    public BuildStageId Id => BuildStageId.InspectIso;

    public string Title => "Inspecting source ISO";

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var staged = context.StagedIsoDirectory
            ?? throw new InvalidOperationException("ISO must be staged before it can be inspected.");

        var wim = Path.Combine(staged, "sources", "install.wim");
        var esd = Path.Combine(staged, "sources", "install.esd");
        var isEsd = !File.Exists(wim) && File.Exists(esd);
        var image = isEsd ? esd : wim;

        if (!File.Exists(image))
        {
            throw new FileNotFoundException("Neither sources\\install.wim nor sources\\install.esd is present.", image);
        }

        var editions = await backend.GetEditionsAsync(image, cancellationToken).ConfigureAwait(false);
        if (editions.Count == 0)
        {
            throw new InvalidDataException("The image contains no editions.");
        }

        context.InstallWimPath = image;
        context.ImageInfo = new WindowsImageInfo
        {
            SourceIsoPath = context.Request.SourceIsoPath,
            IsEsd = isEsd,
            Editions = editions,
            TotalSizeBytes = new FileInfo(image).Length,
        };

        var selected = editions.FirstOrDefault(e => e.Index == context.Request.EditionIndex)
            ?? throw new ArgumentOutOfRangeException(
                nameof(context), $"Edition index {context.Request.EditionIndex} is not in this image.");

        // Refuse media we know we cannot service correctly; warn about media we merely have not
        // validated. See docs/PLAN.md section 1.
        switch (MediaSupportPolicy.Classify(selected.Build))
        {
            case MediaSupport.Unsupported:
                throw new NotSupportedException(
                    $"Build {selected.Build} is older than 24H2 (26100) and is past end of updates. " +
                    "TinyWin supports 24H2 and 25H2.");

            case MediaSupport.Unverified:
                context.Warn(
                    $"Build {selected.Build} is newer than the validated range (26100-26299). " +
                    "The catalog has not been checked against it, so some removals may find no target.");
                break;
        }

        context.BootGeometry = await isoBuilder
            .ReadBootGeometryAsync(context.Request.SourceIsoPath, cancellationToken)
            .ConfigureAwait(false);

        progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            Message = $"{selected.Name} — build {selected.Build}, {selected.Architecture}",
        });
    }
}

/// <summary>Copies the ISO contents to the scratch directory so they can be modified.</summary>
public sealed class StageFilesStage(IIsoBuilder isoBuilder) : IBuildStage
{
    public BuildStageId Id => BuildStageId.StageFiles;

    public string Title => "Copying ISO contents";

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var staged = Path.Combine(context.Request.ScratchDirectory, "iso");
        Directory.CreateDirectory(staged);

        var relay = new Progress<double>(p => progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            StagePercent = p,
            Message = "Copying ISO contents",
        }));

        await isoBuilder
            .ExtractAsync(context.Request.SourceIsoPath, staged, relay, cancellationToken)
            .ConfigureAwait(false);

        context.StagedIsoDirectory = staged;
    }
}

/// <summary>
/// Converts install.esd to install.wim. ESD is solid-compressed and cannot be mounted for
/// servicing, so this is mandatory when the source ships one.
/// </summary>
public sealed class NormalizeImageStage(IImagingBackend backend) : IBuildStage
{
    public BuildStageId Id => BuildStageId.NormalizeImage;

    public string Title => "Converting install.esd to install.wim";

    public bool ShouldRun(BuildContext context) => context?.ImageInfo?.IsEsd ?? false;

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var esd = context.InstallWimPath!;
        var wim = Path.Combine(Path.GetDirectoryName(esd)!, "install.wim");

        var relay = new Progress<double>(p => progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            StagePercent = p,
            Message = "Converting install.esd to install.wim",
        }));

        await backend
            .ExportImageAsync(esd, context.Request.EditionIndex, wim, CompressionType.Maximum, relay, cancellationToken)
            .ConfigureAwait(false);

        File.Delete(esd);
        context.InstallWimPath = wim;

        // The exported WIM holds only the selected edition, so it is now index 1 regardless of
        // where it sat in the ESD. Later stages must not keep using the original index.
        context.EditionIndexOverride = 1;
    }
}

/// <summary>Writes the generated autounattend.xml into the ISO root.</summary>
public sealed class WriteUnattendStage(IUnattendGenerator generator) : IBuildStage
{
    public BuildStageId Id => BuildStageId.WriteUnattend;

    public string Title => "Writing unattend file";

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var staged = context.StagedIsoDirectory
            ?? throw new InvalidOperationException("ISO has not been staged.");

        var architecture = context.ImageInfo?.Editions
            .FirstOrDefault(e => e.Index == context.Request.EditionIndex)?.Architecture ?? "amd64";

        var xml = generator.Generate(context.Request.Unattend, architecture);

        await File.WriteAllTextAsync(
            Path.Combine(staged, "autounattend.xml"), xml, cancellationToken).ConfigureAwait(false);

        progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            Message = "autounattend.xml written",
        });
    }
}

/// <summary>Confirms the output exists and reports the size delta.</summary>
public sealed class VerifyStage : IBuildStage
{
    public BuildStageId Id => BuildStageId.Verify;

    public string Title => "Verifying output";

    public Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var output = context.Request.OutputIsoPath;
        if (!File.Exists(output))
        {
            throw new FileNotFoundException("The ISO builder reported success but produced no file.", output);
        }

        var before = new FileInfo(context.Request.SourceIsoPath).Length;
        var after = new FileInfo(output).Length;

        if (after <= 0)
        {
            throw new InvalidDataException("The output ISO is empty.");
        }

        progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            Message = $"{before / (1024 * 1024)} MB -> {after / (1024 * 1024)} MB",
        });

        return Task.CompletedTask;
    }
}
