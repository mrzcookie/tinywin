using System.Security.Principal;
using TinyWin.Catalog;
using TinyWin.Catalog.Resolution;
using TinyWin.Catalog.Validation;

namespace TinyWin.Cli;

/// <summary>
/// Headless head on the same Core the UI uses. Exists so the destructive, slow pipeline can be
/// exercised without clicking through a GUI — see docs/PLAN.md section 2.
/// </summary>
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        return command switch
        {
            "catalog" => await CatalogCommandAsync(args),
            "presets" => await PresetsCommandAsync(),
            "doctor" => Doctor(),
            "build" => await BuildCommand.RunAsync(args, await LoadCatalogAsync()),
            _ => Help(),
        };
    }

    private static async Task<CatalogDocument> LoadCatalogAsync()
    {
        // Prefer the working-tree catalog when running from a checkout, so catalog authors see
        // their edits without a rebuild. Falls back to the embedded copy in a published binary.
        var repoCatalog = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog");
        return Directory.Exists(repoCatalog)
            ? await new DirectoryCatalogProvider(Path.GetFullPath(repoCatalog)).LoadAsync()
            : await new EmbeddedCatalogProvider().LoadAsync();
    }

    private static async Task<int> CatalogCommandAsync(string[] args)
    {
        var catalog = await LoadCatalogAsync();
        var errors = CatalogValidator.Validate(catalog);

        if (args.Contains("--validate", StringComparer.OrdinalIgnoreCase))
        {
            if (errors.Count == 0)
            {
                Console.WriteLine($"Catalog OK: {catalog.Components.Count} components, {catalog.Presets.Count} presets.");
                return 0;
            }

            Console.Error.WriteLine($"Catalog has {errors.Count} error(s):");
            foreach (var error in errors)
            {
                Console.Error.WriteLine("  " + error);
            }

            return 1;
        }

        foreach (var group in catalog.Components.GroupBy(c => c.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(group.Key);
            foreach (var component in group.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {component.Id,-28} {component.Risk,-15} ~{component.EstimatedSavingsMb} MB");
            }
        }

        return 0;
    }

    private static async Task<int> PresetsCommandAsync()
    {
        var catalog = await LoadCatalogAsync();

        foreach (var preset in catalog.Presets.OrderBy(p => p.Order))
        {
            var ids = PlanResolver.ExpandPreset(catalog, preset.Id);
            var plan = PlanResolver.Resolve(catalog, ids, targetBuild: 26200);

            Console.WriteLine($"{preset.Name} ({preset.Id})");
            Console.WriteLine($"  {preset.Description}");
            Console.WriteLine(
                $"  {plan.ComponentIds.Count} components, ~{plan.EstimatedSavingsMb} MB, highest risk {plan.HighestRisk}");
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>Reports whether this machine can actually run a build. Elevation is the usual gap.</summary>
    private static int Doctor()
    {
        var elevated = OperatingSystem.IsWindows() &&
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        Console.WriteLine($"Elevated:        {(elevated ? "yes" : "NO — DISM will fail with error 740")}");
        Console.WriteLine($"OS:              {Environment.OSVersion.Version}");

        var scratch = Path.GetPathRoot(AppContext.BaseDirectory);
        if (scratch is not null)
        {
            var free = new DriveInfo(scratch).AvailableFreeSpace / (1024L * 1024 * 1024);
            Console.WriteLine($"Free space:      {free} GB on {scratch} (need ~25 GB)");
        }

        return elevated ? 0 : 1;
    }

    private static int Help()
    {
        Console.WriteLine("""
            tinywin — headless runner (development use)

              tinywin catalog [--validate]   List catalog components, or validate them
              tinywin presets                Show each preset and what it resolves to
              tinywin doctor                 Check whether this machine can run a build
              tinywin build --iso <path>     Build a customised ISO

            build options:
              --iso     <path>   Source Windows 11 ISO                        (required)
              --out     <path>   Output ISO         (default: <source>-tiny.iso)
              --preset  <id>     minimal | balanced | aggressive | core  (default: balanced)
              --index   <n>      Edition index within install.wim              (default: 1)
              --scratch <path>   Working directory, needs ~25 GB free
              --dry-run          Resolve and print the plan without changing anything
              --resume           Continue from the last checkpoint

            Requires Administrator: DISM returns error 740 otherwise. See docs/PLAN.md.
            """);
        return 0;
    }
}
