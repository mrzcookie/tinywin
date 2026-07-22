using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using CatalogHive = TinyWin.Catalog.Models.RegistryHive;
using CatalogValueKind = TinyWin.Catalog.Models.RegistryValueKind;
using Win32Registry = Microsoft.Win32.Registry;

namespace TinyWin.Registry.Tests.Elevated;

/// <summary>
/// The half of this project that unit tests cannot reach: the P/Invoke layer, the privilege
/// adjustment, and a real <c>RegLoadKey</c> / <c>RegUnLoadKey</c> round trip.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>TINYWIN_ELEVATED_TESTS=1</c> and the process is elevated, so an ordinary
/// <c>dotnet test</c> run is unaffected. Run them with <c>scripts/verify-offline-registry.ps1</c>
/// from an administrator prompt.
/// </para>
/// <para>
/// They deliberately do not need a mounted WIM. A hive saved out of <c>HKCU</c> with
/// <c>reg save</c> is a real hive file, and loading it exercises exactly the same code path as an
/// image's SOFTWARE hive would — which means the risky part can be verified on any machine, not
/// only one with 25 GB free and an ISO to hand. Set <c>TINYWIN_MOUNT_PATH</c> to also run the last
/// test against a genuinely mounted image.
/// </para>
/// </remarks>
[Trait("Category", "Elevated")]
public class ElevatedHiveTests
{
    private const string ProbeKey = @"Software\TinyWinVerify";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("TINYWIN_ELEVATED_TESTS") == "1";

    private static bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [Fact]
    public async Task Load_apply_and_unload_round_trip_against_a_real_hive_file()
    {
        RequireElevation();
        using var image = SyntheticImage.Create();

        var registry = new OfflineRegistry();
        var session = await registry.OpenAsync(image.MountPath, [CatalogHive.Software], TestContext.Current.CancellationToken);

        try
        {
            Assert.Contains("zTW-SOFTWARE", Win32Registry.LocalMachine.GetSubKeyNames());

            var setStatus = await Apply(session, "verify.set", new ComponentAction
            {
                Type = ActionType.SetRegistry,
                Hive = CatalogHive.Software,
                Key = @"Policies\TinyWin",
                ValueName = "Enabled",
                Kind = CatalogValueKind.Dword,
                Data = JsonDocument.Parse("1").RootElement.Clone(),
            });

            Assert.Equal(ActionStatus.Applied, setStatus);

            // Deleting the same key twice is the no-op contract from docs/PLAN.md section 2.1,
            // exercised here against a real hive rather than a fake.
            Assert.Equal(ActionStatus.Applied, await Apply(session, "verify.delete", Delete(@"Policies\TinyWin")));
            Assert.Equal(ActionStatus.NoTarget, await Apply(session, "verify.delete", Delete(@"Policies\TinyWin")));
            Assert.Equal(ActionStatus.NoTarget, await Apply(session, "verify.delete", Delete(@"Nothing\Here")));
        }
        finally
        {
            await session.DisposeAsync();
        }

        // The assertion this whole project exists for.
        Assert.DoesNotContain("zTW-SOFTWARE", Win32Registry.LocalMachine.GetSubKeyNames());
    }

    [Fact]
    public async Task Writes_survive_the_unload_and_are_visible_to_reg_exe()
    {
        RequireElevation();
        using var image = SyntheticImage.Create();

        var registry = new OfflineRegistry();
        await using (var session = await registry.OpenAsync(image.MountPath, [CatalogHive.Software], TestContext.Current.CancellationToken))
        {
            await Apply(session, "verify.set", new ComponentAction
            {
                Type = ActionType.SetRegistry,
                Hive = CatalogHive.Software,
                Key = @"Policies\TinyWin",
                ValueName = "Marker",
                Kind = CatalogValueKind.Sz,
                Data = JsonDocument.Parse("\"tinywin-was-here\"").RootElement.Clone(),
            });
        }

        // Verified with an independent tool, so a bug in our own read path cannot mask a bug in
        // our write path.
        Reg("load", @"HKLM\zTWVerify", image.HiveFilePath);
        try
        {
            var output = Reg("query", @"HKLM\zTWVerify\Policies\TinyWin", "/v", "Marker");
            Assert.Contains("tinywin-was-here", output, StringComparison.Ordinal);
        }
        finally
        {
            Reg("unload", @"HKLM\zTWVerify");
        }
    }

    [Fact]
    public async Task Stranded_hive_recovery_unloads_a_leftover_from_a_previous_run()
    {
        RequireElevation();
        using var image = SyntheticImage.Create();

        // Stand in for a crashed run: load the hive behind TinyWin's back, under our prefix.
        Reg("load", @"HKLM\zTW-SOFTWARE", image.HiveFilePath);

        var unloaded = await new OfflineRegistry().UnloadStrandedHivesAsync(TestContext.Current.CancellationToken);

        Assert.True(unloaded >= 1, $"Expected at least one stranded hive to be unloaded, got {unloaded}.");
        Assert.DoesNotContain("zTW-SOFTWARE", Win32Registry.LocalMachine.GetSubKeyNames());
    }

    [Fact]
    public async Task All_five_hives_load_and_unload_from_a_mounted_image()
    {
        RequireElevation();

        var mountPath = Environment.GetEnvironmentVariable("TINYWIN_MOUNT_PATH");
        if (string.IsNullOrWhiteSpace(mountPath) || !Directory.Exists(mountPath))
        {
            Assert.Skip("Set TINYWIN_MOUNT_PATH to a mounted Windows image to run this test.");
            return;
        }

        var registry = new OfflineRegistry();
        var session = await registry.OpenAsync(mountPath, Enum.GetValues<CatalogHive>(), TestContext.Current.CancellationToken);

        Assert.Equal(5, session.LoadedHives.Count);
        await session.DisposeAsync();

        Assert.DoesNotContain(
            Win32Registry.LocalMachine.GetSubKeyNames(),
            n => n.StartsWith("zTW-", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<ActionStatus> Apply(IHiveSession session, string componentId, ComponentAction action) =>
        session.ApplyAsync(componentId, action, TestContext.Current.CancellationToken);

    private static ComponentAction Delete(string key) => new()
    {
        Type = ActionType.DeleteRegistryKey,
        Hive = CatalogHive.Software,
        Key = key,
    };

    private static void RequireElevation()
    {
        if (!Enabled)
        {
            Assert.Skip("Set TINYWIN_ELEVATED_TESTS=1 to run the elevated registry tests.");
        }

        if (!IsElevated)
        {
            Assert.Skip("These tests need an elevated process — RegLoadKey cannot work without it.");
        }
    }

    private static string Reg(params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo("reg.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }.With(args)) ?? throw new InvalidOperationException("Could not start reg.exe.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0
            ? stdout
            : throw new InvalidOperationException($"reg {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}{stdout}");
    }

    /// <summary>
    /// A directory shaped like a mounted image, with a real hive file where the SOFTWARE hive
    /// would be. Produced with <c>reg save</c> so it is a genuine hive, not a fixture.
    /// </summary>
    private sealed class SyntheticImage : IDisposable
    {
        private SyntheticImage(string root) => MountPath = root;

        public string MountPath { get; }

        public string HiveFilePath => Path.Combine(MountPath, @"Windows\System32\config\SOFTWARE");

        public static SyntheticImage Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinywin-verify-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, @"Windows\System32\config"));

            using (var seed = Win32Registry.CurrentUser.CreateSubKey(ProbeKey))
            {
                seed.SetValue("Seed", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }

            var image = new SyntheticImage(root);
            Reg("save", $@"HKCU\{ProbeKey}", image.HiveFilePath, "/y");
            Win32Registry.CurrentUser.DeleteSubKeyTree(ProbeKey, throwOnMissingSubKey: false);

            return image;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(MountPath, recursive: true);
            }
            catch (IOException)
            {
                // A hive we failed to unload keeps its file locked. That failure is already being
                // reported by the test itself; masking it with a cleanup error helps nobody.
            }
        }
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo With(this ProcessStartInfo info, params string[] args)
    {
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return info;
    }
}
