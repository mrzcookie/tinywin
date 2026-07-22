using TinyWin.Catalog.Models;
using TinyWin.Registry.Tests.Fakes;

namespace TinyWin.Registry.Tests;

/// <summary>
/// The lifetime tests. These are the ones that matter: docs/PLAN.md section 3.3 is entirely about
/// what happens when a hive will not unload, and every behaviour described there is asserted here
/// against <see cref="FakeNativeRegistry"/>.
/// </summary>
public class OfflineRegistryTests
{
    private static OfflineRegistry Create(FakeNativeRegistry native, int maxAttempts = 6) =>
        new(native, HiveUnloadPolicy.Immediate(maxAttempts));

    [Fact]
    public async Task Privileges_are_enabled_before_the_first_load()
    {
        // Enabling SeBackupPrivilege/SeRestorePrivilege after RegLoadKey would be useless, and
        // skipping it is the single most common cause of ERROR_ACCESS_DENIED.
        var native = new FakeNativeRegistry();

        await using var session = await Create(native).OpenAsync(@"C:\mount", [RegistryHive.Software], TestContext.Current.CancellationToken);

        Assert.Equal("privileges", native.Calls[0]);
        Assert.StartsWith("load:", native.Calls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loads_every_requested_hive_from_the_mount_path()
    {
        var native = new FakeNativeRegistry();

        await using var session = await Create(native).OpenAsync(
            @"C:\mount", [RegistryHive.Software, RegistryHive.System], TestContext.Current.CancellationToken);

        Assert.Contains(@"load:zTW-SOFTWARE:C:\mount\Windows\System32\config\SOFTWARE", native.Calls);
        Assert.Contains(@"load:zTW-SYSTEM:C:\mount\Windows\System32\config\SYSTEM", native.Calls);
        Assert.Equal(new[] { RegistryHive.Software, RegistryHive.System }, session.LoadedHives);
    }

    [Fact]
    public async Task A_hive_requested_twice_is_loaded_once()
    {
        var native = new FakeNativeRegistry();

        await using var session = await Create(native).OpenAsync(
            @"C:\mount", [RegistryHive.Software, RegistryHive.Software], TestContext.Current.CancellationToken);

        Assert.Single(session.LoadedHives);
        Assert.Single(native.Calls, c => c.StartsWith("load:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_unloads_in_reverse_load_order()
    {
        var native = new FakeNativeRegistry();
        var session = await Create(native).OpenAsync(
            @"C:\mount", [RegistryHive.Software, RegistryHive.System, RegistryHive.Default], TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        Assert.Equal(new[] { "zTW-SYSTEM", "zTW-SOFTWARE", "zTW-DEFAULT" }, native.UnloadOrder);
        Assert.Empty(native.Loaded);
        Assert.Empty(session.LoadedHives);
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var native = new FakeNativeRegistry();
        var session = await Create(native).OpenAsync(@"C:\mount", [RegistryHive.Software], TestContext.Current.CancellationToken);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, native.UnloadAttempts);
    }

    [Fact]
    public async Task A_hive_that_refuses_once_is_unloaded_on_retry()
    {
        var native = new FakeNativeRegistry();
        native.UnloadFailuresRemaining["zTW-SOFTWARE"] = 2;
        var session = await Create(native).OpenAsync(@"C:\mount", [RegistryHive.Software], TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        Assert.Equal(3, native.UnloadAttempts);
        Assert.Empty(native.Loaded);
    }

    [Fact]
    public async Task A_hive_that_never_unloads_throws_rather_than_reporting_success()
    {
        // The non-negotiable. Reporting a clean teardown here hands the user a machine whose WIM
        // cannot be dismounted and no explanation for it.
        var native = new FakeNativeRegistry();
        native.UnloadFailuresRemaining["zTW-SOFTWARE"] = int.MaxValue;
        var session = await Create(native).OpenAsync(@"C:\mount", [RegistryHive.Software], TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<HiveUnloadException>(async () => await session.DisposeAsync());

        Assert.Equal(new[] { "zTW-SOFTWARE" }, ex.MountNames);
        Assert.Contains("reg unload", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task A_stuck_hive_stays_listed_so_the_caller_can_see_what_is_stranded()
    {
        var native = new FakeNativeRegistry();
        native.UnloadFailuresRemaining["zTW-SOFTWARE"] = int.MaxValue;
        var session = await Create(native).OpenAsync(@"C:\mount", [RegistryHive.Software, RegistryHive.System], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<HiveUnloadException>(async () => await session.DisposeAsync());

        // SYSTEM still came down — one stuck hive must not strand the rest.
        Assert.Equal(new[] { RegistryHive.Software }, session.LoadedHives);
    }

    [Fact]
    public async Task Retries_stop_at_the_configured_attempt_count()
    {
        var native = new FakeNativeRegistry();
        native.UnloadFailuresRemaining["zTW-SOFTWARE"] = int.MaxValue;
        var session = await Create(native, maxAttempts: 3).OpenAsync(@"C:\mount", [RegistryHive.Software], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<HiveUnloadException>(async () => await session.DisposeAsync());

        Assert.Equal(3, native.UnloadAttempts);
    }

    [Fact]
    public async Task A_failed_load_unwinds_the_hives_that_already_came_up()
    {
        var native = new FakeNativeRegistry();
        native.LoadFailures["zTW-SYSTEM"] = "hive file is corrupt";

        var ex = await Assert.ThrowsAsync<RegistryOperationException>(
            () => Create(native).OpenAsync(@"C:\mount", [RegistryHive.Software, RegistryHive.System], TestContext.Current.CancellationToken));

        Assert.Equal("hive file is corrupt", ex.Message);
        Assert.Empty(native.Loaded);
        Assert.Equal(new[] { "zTW-SOFTWARE" }, native.UnloadOrder);
    }

    [Fact]
    public async Task A_failed_load_whose_cleanup_also_fails_reports_both()
    {
        var native = new FakeNativeRegistry();
        native.LoadFailures["zTW-SYSTEM"] = "hive file is corrupt";
        native.UnloadFailuresRemaining["zTW-SOFTWARE"] = int.MaxValue;

        var ex = await Assert.ThrowsAsync<RegistryOperationException>(
            () => Create(native).OpenAsync(@"C:\mount", [RegistryHive.Software, RegistryHive.System], TestContext.Current.CancellationToken));

        Assert.Contains("hive file is corrupt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Cleanup then failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_partway_through_a_load_unwinds_what_was_loaded()
    {
        var native = new FakeNativeRegistry();
        using var cts = new CancellationTokenSource();
        native.OnLoad = _ => cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Create(native).OpenAsync(@"C:\mount", [RegistryHive.Software, RegistryHive.System], cts.Token));

        Assert.Empty(native.Loaded);
        Assert.Equal(new[] { "zTW-SOFTWARE" }, native.UnloadOrder);
    }

    [Fact]
    public async Task Open_rejects_a_blank_mount_path() =>
        await Assert.ThrowsAsync<ArgumentException>(
            () => Create(new FakeNativeRegistry()).OpenAsync("  ", [RegistryHive.Software], TestContext.Current.CancellationToken));

    [Fact]
    public async Task Stranded_recovery_unloads_only_our_own_mount_points()
    {
        var native = new FakeNativeRegistry();
        native.ExtraHklmSubkeys.AddRange(["zTW-SOFTWARE", "zTW-SYSTEM", "zSOFTWARE", "BCD00000000"]);

        var unloaded = await Create(native).UnloadStrandedHivesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, unloaded);
        Assert.Equal(new[] { "zTW-SOFTWARE", "zTW-SYSTEM" }, native.UnloadOrder);

        // Another tool's mount point is not ours to unload.
        Assert.Contains("zSOFTWARE", native.ExtraHklmSubkeys);
    }

    [Fact]
    public async Task Stranded_recovery_reports_zero_on_a_clean_machine()
    {
        var native = new FakeNativeRegistry();

        Assert.Equal(0, await Create(native).UnloadStrandedHivesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, native.UnloadAttempts);
    }

    [Fact]
    public async Task Stranded_recovery_enables_privileges_first()
    {
        var native = new FakeNativeRegistry();
        native.ExtraHklmSubkeys.Add("zTW-SOFTWARE");

        await Create(native).UnloadStrandedHivesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("privileges", native.Calls[0]);
    }

    [Fact]
    public async Task Stranded_recovery_throws_when_a_leftover_hive_will_not_come_down()
    {
        // Preflight has to fail here rather than let a build start that is guaranteed to fail at
        // dismount twenty minutes later.
        var native = new FakeNativeRegistry();
        native.ExtraHklmSubkeys.Add("zTW-SOFTWARE");
        native.UnloadFailuresRemaining["zTW-SOFTWARE"] = int.MaxValue;

        var ex = await Assert.ThrowsAsync<HiveUnloadException>(() => Create(native).UnloadStrandedHivesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new[] { "zTW-SOFTWARE" }, ex.MountNames);
    }
}
