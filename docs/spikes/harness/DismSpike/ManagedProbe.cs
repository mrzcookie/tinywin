using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Dism;

static class ManagedProbe
{
    public static void Run(string[] args)
    {
        DumpInternalStruct();
        if (args.Contains("--no-live")) return;
        Live();
    }

    /// <summary>What layout does Microsoft.Dism itself marshal the undocumented struct with?</summary>
    static void DumpInternalStruct()
    {
        var asm = typeof(DismApi).Assembly;
        var all = asm.GetTypes().Where(t => t.IsValueType && !t.IsEnum && t.StructLayoutAttribute is not null).ToList();
        Console.WriteLine("=== Pack setting across ALL native structs in Microsoft.Dism ===");
        foreach (var g in all.GroupBy(t => t.StructLayoutAttribute!.Pack).OrderBy(g => g.Key))
            Console.WriteLine($"  Pack={g.Key}: {g.Count()} structs — {string.Join(", ", g.Select(t => t.Name).Take(30))}");
        Console.WriteLine();

        var candidates = all
            .Where(t => t.Name.Contains("Appx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine("=== internal native structs in Microsoft.Dism matching *Appx* ===");
        foreach (var t in candidates)
        {
            Console.WriteLine($"{t.FullName}  (Marshal.SizeOf={SafeSize(t)}, {t.StructLayoutAttribute?.Value}, " +
                              $"Pack={t.StructLayoutAttribute?.Pack}, charset {t.StructLayoutAttribute?.CharSet})");
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                int off = -1;
                try { off = (int)Marshal.OffsetOf(t, f.Name); } catch { }
                var ma = f.GetCustomAttribute<MarshalAsAttribute>();
                Console.WriteLine($"   +0x{off:X2}  {f.FieldType.Name,-12} {f.Name}{(ma is null ? "" : $"  [MarshalAs({ma.Value})]")}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("=== public DismAppxPackage properties ===");
        foreach (var p in typeof(DismAppxPackage).GetProperties())
            Console.WriteLine($"   {p.PropertyType.Name,-24} {p.Name}");
        Console.WriteLine();
    }

    static int SafeSize(Type t) { try { return Marshal.SizeOf(t); } catch { return -1; } }

    static void Live()
    {
        Console.WriteLine("=== live call against DISM_ONLINE_IMAGE via Microsoft.Dism ===");
        DismApi.Initialize(DismLogLevel.LogErrorsWarningsInfo);
        try
        {
            var swOpen = System.Diagnostics.Stopwatch.StartNew();
            using var session = DismApi.OpenOnlineSession();
            swOpen.Stop();
            Console.WriteLine($"OpenOnlineSession: {swOpen.Elapsed.TotalMilliseconds:F1} ms");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pkgs = DismApi.GetProvisionedAppxPackages(session);
            sw.Stop();
            Console.WriteLine($"GetProvisionedAppxPackages: {pkgs.Count} packages in {sw.Elapsed.TotalMilliseconds:F1} ms");
            Console.WriteLine();

            foreach (var p in pkgs.Take(8))
                Console.WriteLine($"  DisplayName={p.DisplayName}\n    PackageName={p.PackageName}\n    Publisher={p.PublisherId} Arch={p.Architecture} Ver={p.Version} ResourceId='{p.ResourceId}'\n    InstallLocation={p.InstallLocation}\n");

            Console.WriteLine($"(total {pkgs.Count}; sanity: all PackageName non-empty = {pkgs.All(x => !string.IsNullOrWhiteSpace(x.PackageName))})");

            // repeat timings, warm
            var times = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var s2 = System.Diagnostics.Stopwatch.StartNew();
                _ = DismApi.GetProvisionedAppxPackages(session);
                s2.Stop();
                times.Add(s2.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"warm repeats (ms): {string.Join(", ", times.Select(t => t.ToString("F1")))}");
        }
        finally { DismApi.Shutdown(); }
    }
}
