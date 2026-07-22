using System.Diagnostics;
using TinyWin.Catalog;
using TinyWin.Catalog.Resolution;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;
using TinyWin.Imaging;
using TinyWin.IsoBuilder;
using TinyWin.Registry;
using TinyWin.Unattend;

namespace TinyWin.Cli;

/// <summary>
/// Runs a real build. The M1 vertical slice, and the same Core the UI drives.
/// </summary>
internal static class BuildCommand
{
    public static async Task<int> RunAsync(string[] args, CatalogDocument catalog)
    {
        var iso = GetOption(args, "--iso");
        var output = GetOption(args, "--out");
        var presetId = GetOption(args, "--preset") ?? "balanced";
        var scratch = GetOption(args, "--scratch") ?? Path.Combine(Path.GetTempPath(), "tinywin-build");
        var indexText = GetOption(args, "--index") ?? "1";
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

        if (iso is null)
        {
            Console.Error.WriteLine("--iso is required.");
            return 2;
        }

        if (!int.TryParse(indexText, out var index))
        {
            Console.Error.WriteLine($"--index must be a number, got '{indexText}'.");
            return 2;
        }

        output ??= Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(iso))!,
            Path.GetFileNameWithoutExtension(iso) + "-tiny.iso");

        IReadOnlyList<string> componentIds;
        try
        {
            componentIds = PlanResolver.ExpandPreset(catalog, presetId);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Available presets: " + string.Join(", ", catalog.Presets.Select(p => p.Id)));
            return 2;
        }

        var request = new BuildRequest
        {
            SourceIsoPath = Path.GetFullPath(iso),
            OutputIsoPath = Path.GetFullPath(output),
            EditionIndex = index,
            ComponentIds = componentIds,
            ScratchDirectory = Path.GetFullPath(scratch),
            Resume = args.Contains("--resume", StringComparer.OrdinalIgnoreCase),
        };

        // Resolve against the primary target build. The real number is read during Inspect, which
        // re-checks and warns; this only shapes the plan we go in with.
        var plan = PlanResolver.Resolve(catalog, componentIds, MediaSupportPolicy.MinimumSupportedBuild + 100);

        Console.WriteLine($"Source     : {request.SourceIsoPath}");
        Console.WriteLine($"Output     : {request.OutputIsoPath}");
        Console.WriteLine($"Preset     : {presetId} ({plan.ComponentIds.Count} components)");
        Console.WriteLine($"Risk       : {plan.HighestRisk}");
        Console.WriteLine($"Scratch    : {request.ScratchDirectory}");
        Console.WriteLine();

        foreach (var warning in plan.Warnings)
        {
            Console.WriteLine($"  warning: [{warning.ComponentId}] {warning.Message}");
        }

        if (dryRun)
        {
            Console.WriteLine($"Dry run — {plan.ImageActions.Count} image actions, " +
                              $"{plan.RegistryActions.Count} registry actions. Nothing was changed.");
            return 0;
        }

        // Unserviceable selections destroy the image's ability to be repaired, so the CLI asks for
        // the same deliberate confirmation the UI's Review page does.
        if (plan.HighestRisk == Catalog.Models.RiskTier.Unserviceable &&
            !args.Contains("--i-understand-this-is-unserviceable", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "This selection removes servicing components. The resulting image can never be " +
                "updated, repaired, or have features added.");
            Console.Error.WriteLine("Re-run with --i-understand-this-is-unserviceable to proceed.");
            return 3;
        }

        using var imaging = new DismExeBackend();
        var pipeline = BuildPipelineFactory.Create(
            imaging, new OfflineRegistry(), new IsoBuilderService(), new UnattendGenerator());

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nCancelling — unwinding so the image is not left mounted...");
            cts.Cancel();
        };

        var context = new BuildContext { Request = request, Plan = plan };
        var progress = new Progress<BuildProgress>(Render);
        var report = await pipeline.RunAsync(context, progress, cts.Token);

        PrintReport(report);
        return report.Succeeded ? 0 : 1;
    }

    private static BuildStageId? _lastStage;

    private static void Render(BuildProgress p)
    {
        if (_lastStage != p.Stage)
        {
            Console.WriteLine();
            Console.WriteLine($"[{p.Stage}] {p.Message}");
            _lastStage = p.Stage;
            return;
        }

        if (p.StagePercent is { } pct)
        {
            Console.Write($"\r    {pct * 100,5:0.0}%  {Truncate(p.Message, 60),-60}");
        }
        else
        {
            Console.WriteLine($"    {p.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void PrintReport(BuildReport report)
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 70));
        Console.WriteLine(report.Succeeded ? "BUILD SUCCEEDED" : "BUILD FAILED");
        Console.WriteLine($"Elapsed: {report.TotalDuration:hh\\:mm\\:ss}");
        Console.WriteLine();

        foreach (var stage in report.Stages)
        {
            var mark = stage.State switch
            {
                StageState.Completed => "ok  ",
                StageState.Skipped => "skip",
                StageState.Failed => "FAIL",
                _ => "?   ",
            };

            Console.WriteLine($"  {mark} {stage.Stage,-18} {stage.Duration:mm\\:ss}" +
                              (stage.Error is null ? "" : $"  {stage.Error}"));
        }

        Console.WriteLine();
        Console.WriteLine($"  applied   : {report.AppliedCount}");
        Console.WriteLine($"  no target : {report.NoTargetCount}");
        Console.WriteLine($"  failed    : {report.FailedCount}");

        // A high no-op count means the catalog has drifted from the media. Surfaced rather than
        // buried — see docs/PLAN.md section 2.1.
        if (report.NoTargetCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Actions that found no target (catalog may have drifted):");
            foreach (var group in report.Actions
                         .Where(a => a.Status == ActionStatus.NoTarget)
                         .GroupBy(a => a.ComponentId)
                         .OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"    {group.Key,-30} {group.Count()}");
            }
        }

        if (report.FailedCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Failures:");
            foreach (var failure in report.Actions.Where(a => a.Status == ActionStatus.Failed))
            {
                Console.WriteLine($"    [{failure.ComponentId}] {failure.Description}: {failure.Detail}");
            }
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"  warning: {warning}");
        }

        if (report.Succeeded)
        {
            Console.WriteLine();
            Console.WriteLine($"  Output: {report.OutputIsoPath}");
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
