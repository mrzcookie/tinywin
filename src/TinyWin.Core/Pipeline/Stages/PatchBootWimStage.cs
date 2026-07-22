using System.Text.Json;
using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;

namespace TinyWin.Core.Pipeline.Stages;

/// <summary>
/// Applies the hardware-requirement bypasses to <c>boot.wim</c> index 2, the Windows Setup
/// environment.
/// </summary>
/// <remarks>
/// These have to go into boot.wim, not install.wim. The checks that refuse to install on
/// unsupported hardware run inside Setup itself, before install.wim is ever touched, so patching
/// only the installed image leaves Setup still refusing at the very first screen.
///
/// autounattend.xml carries the same values, but that is a belt-and-braces duplicate: an answer
/// file has to be found and parsed, whereas <c>LabConfig</c> is read directly by Setup. tiny11 does
/// both for the same reason.
///
/// Index 2 specifically: index 1 is Windows PE, index 2 is Windows Setup. Only index 2 runs the
/// compatibility checks.
/// </remarks>
public sealed class PatchBootWimStage(IImagingBackend backend, IOfflineRegistry registry) : IBuildStage
{
    private const int SetupImageIndex = 2;

    public BuildStageId Id => BuildStageId.PatchBootWim;

    public string Title => "Applying setup bypasses to boot.wim";

    public bool ShouldRun(BuildContext context)
    {
        if (context is null)
        {
            return false;
        }

        var u = context.Request.Unattend;
        return u.BypassTpmCheck || u.BypassSecureBootCheck || u.BypassRamCheck ||
               u.BypassCpuCheck || u.BypassStorageCheck;
    }

    public async Task ExecuteAsync(
        BuildContext context, IProgress<BuildProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var staged = context.StagedIsoDirectory
            ?? throw new InvalidOperationException("ISO has not been staged.");

        var bootWim = Path.Combine(staged, "sources", "boot.wim");
        if (!File.Exists(bootWim))
        {
            context.Warn("sources\\boot.wim is missing; setup bypasses were not applied.");
            return;
        }

        var mountPath = Path.Combine(context.Request.ScratchDirectory, "bootmount");
        Directory.CreateDirectory(mountPath);

        var relay = new Progress<double>(p => progress.Report(new BuildProgress
        {
            Stage = Id,
            State = StageState.Running,
            StagePercent = p,
            Message = "Mounting boot.wim",
        }));

        var image = await backend
            .MountAsync(bootWim, SetupImageIndex, mountPath, relay, cancellationToken)
            .ConfigureAwait(false);

        var committed = false;
        try
        {
            await using (var session = await registry
                .OpenAsync(mountPath, [RegistryHive.System], cancellationToken).ConfigureAwait(false))
            {
                foreach (var action in BuildBypassActions(context.Request.Unattend))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var status = await session
                        .ApplyAsync("setup.bypass", action, cancellationToken)
                        .ConfigureAwait(false);

                    context.Record(new ActionOutcome
                    {
                        ComponentId = "setup.bypass",
                        Description = $"boot.wim: set {action.Key}\\{action.ValueName}",
                        Status = status,
                    });
                }
            }

            await backend.UnmountAsync(image, commit: true, relay, cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            // If the hive work threw, the image is still mounted and must be discarded — otherwise
            // boot.wim stays locked and the ISO cannot be built.
            if (!committed)
            {
                try
                {
                    await backend.UnmountAsync(image, commit: false, cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    context.Warn($"boot.wim could not be dismounted after a failure: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// LabConfig is what Setup reads for the individual hardware checks; MoSetup covers the
    /// in-place upgrade path, which consults a different value.
    /// </summary>
    private static IEnumerable<ComponentAction> BuildBypassActions(UnattendOptions options)
    {
        const string labConfig = @"Setup\LabConfig";

        if (options.BypassTpmCheck)
        {
            yield return Dword(labConfig, "BypassTPMCheck", 1);
        }

        if (options.BypassSecureBootCheck)
        {
            yield return Dword(labConfig, "BypassSecureBootCheck", 1);
        }

        if (options.BypassRamCheck)
        {
            yield return Dword(labConfig, "BypassRAMCheck", 1);
        }

        if (options.BypassStorageCheck)
        {
            yield return Dword(labConfig, "BypassStorageCheck", 1);
        }

        if (options.BypassCpuCheck)
        {
            yield return Dword(labConfig, "BypassCPUCheck", 1);
        }

        if (options.BypassTpmCheck || options.BypassCpuCheck)
        {
            yield return Dword(@"Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", 1);
        }
    }

    private static ComponentAction Dword(string key, string name, int value) => new()
    {
        Type = ActionType.SetRegistry,
        Hive = RegistryHive.System,
        Key = key,
        ValueName = name,
        Kind = RegistryValueKind.Dword,
        Data = JsonDocument.Parse(value.ToString()).RootElement,
    };
}
