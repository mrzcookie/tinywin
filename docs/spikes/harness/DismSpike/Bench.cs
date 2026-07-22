using System.Diagnostics;
using System.Text;
using Microsoft.Dism;

static class Bench
{
    public static void Run(string[] args)
    {
        int n = 5;
        var outDir = AppContext.BaseDirectory;

        Console.WriteLine("=== enumeration benchmark: dism.exe vs native API ===");
        Console.WriteLine($"iterations: {n}\n");

        // --- dism.exe ---
        var exeTimes = new List<double>();
        string lastOut = "";
        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            lastOut = RunProc("dism.exe", "/Online /Get-ProvisionedAppxPackages /English");
            sw.Stop();
            exeTimes.Add(sw.Elapsed.TotalMilliseconds);
            Console.WriteLine($"  dism.exe  run {i + 1}: {sw.Elapsed.TotalMilliseconds:F0} ms");
        }
        File.WriteAllText(Path.Combine(outDir, "dism-exe-getprovisioned.txt"), lastOut);
        Console.WriteLine($"  -> raw output saved ({lastOut.Length} chars), " +
                          $"{lastOut.Split('\n').Count(l => l.StartsWith("PackageName"))} PackageName lines");
        Console.WriteLine();

        // --- native ---
        var apiTimes = new List<double>();
        var apiTimesWarm = new List<double>();
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            DismApi.Initialize(DismLogLevel.LogErrors);
            try
            {
                using var s = DismApi.OpenOnlineSession();
                var pkgs = DismApi.GetProvisionedAppxPackages(s);
                count = pkgs.Count;
                sw.Stop();
                apiTimes.Add(sw.Elapsed.TotalMilliseconds);

                // warm: session already open, just the enumerate call
                var sw2 = Stopwatch.StartNew();
                _ = DismApi.GetProvisionedAppxPackages(s);
                sw2.Stop();
                apiTimesWarm.Add(sw2.Elapsed.TotalMilliseconds);
            }
            finally { DismApi.Shutdown(); }
            Console.WriteLine($"  native    run {i + 1}: {apiTimes[^1]:F0} ms full cycle (init+session+enum), " +
                              $"{apiTimesWarm[^1]:F0} ms enum-only");
        }

        Console.WriteLine();
        Console.WriteLine($"packages found: dism.exe={lastOut.Split('\n').Count(l => l.StartsWith("PackageName"))}  native={count}");
        Console.WriteLine($"median dism.exe        : {Median(exeTimes):F0} ms");
        Console.WriteLine($"median native full     : {Median(apiTimes):F0} ms");
        Console.WriteLine($"median native enum-only: {Median(apiTimesWarm):F0} ms");
    }

    static double Median(List<double> xs) { var c = xs.OrderBy(x => x).ToList(); return c[c.Count / 2]; }

    static string RunProc(string exe, string cmdline)
    {
        var psi = new ProcessStartInfo(exe, cmdline)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }
}
