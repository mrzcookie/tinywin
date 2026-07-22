using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Registry.Tests.Fakes;

namespace TinyWin.Registry.Tests;

/// <summary>
/// Scheduled task removal as a TaskCache edit — see docs/catalog-gaps.md section 3.1 for why
/// deleting the task's XML file offline does not work.
/// </summary>
public class ScheduledTaskTests
{
    private const string Mount = "zTW-SOFTWARE";
    private const string TaskName = @"Microsoft\Windows\Application Experience\ProgramDataUpdater";
    private const string TaskId = "{0600DD45-FAF2-4131-A006-0B17509B9F78}";

    private static async Task<(FakeNativeRegistry Native, IHiveSession Session)> OpenAsync()
    {
        var native = new FakeNativeRegistry();
        var registry = new OfflineRegistry(native, HiveUnloadPolicy.Immediate());
        var session = await registry.OpenAsync(
            @"C:\mount", [RegistryHive.Software], TestContext.Current.CancellationToken);
        return (native, session);
    }

    private static ComponentAction Remove(string name) =>
        new() { Type = ActionType.RemoveScheduledTask, Name = name };

    private static Task<ActionStatus> Apply(IHiveSession session, ComponentAction action) =>
        session.ApplyAsync("system.telemetry", action, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", @"Microsoft\Windows\Defrag\ScheduledDefrag")]
    [InlineData(@"Microsoft\Windows\Defrag\ScheduledDefrag", @"Microsoft\Windows\Defrag\ScheduledDefrag")]
    [InlineData("/Microsoft/Windows/Defrag/", @"Microsoft\Windows\Defrag")]
    [InlineData(@"\\Microsoft\\Defrag", @"Microsoft\Defrag")]
    public void Task_names_normalize_to_a_tree_relative_path(string input, string expected) =>
        Assert.Equal(expected, TaskCache.NormalizeTaskName(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\")]
    [InlineData(@"Microsoft\..\..\Other")]
    public void A_malformed_task_name_is_rejected(string? input) =>
        Assert.Throws<RegistryActionException>(() => TaskCache.NormalizeTaskName(input));

    [Fact]
    public async Task Removing_a_registered_task_deletes_both_halves_of_the_registration()
    {
        var (native, session) = await OpenAsync();
        native.SeedTask(Mount, TaskName, TaskId, "Tasks", "Plain");

        Assert.Equal(ActionStatus.Applied, await Apply(session, Remove($@"\{TaskName}")));

        Assert.False(native.KeyExists($@"{Mount}\{TaskCache.TreePath(TaskName)}"));
        Assert.False(native.KeyExists($@"{Mount}\{TaskCache.IdKeyedPath("Tasks", TaskId)}"));
        Assert.False(native.KeyExists($@"{Mount}\{TaskCache.IdKeyedPath("Plain", TaskId)}"));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task The_tree_node_is_deleted_last_so_a_crash_partway_is_recoverable()
    {
        // Tree is the only thing mapping a task name to its GUID. Deleting it first would leave
        // the Tasks entry unreachable for a re-run.
        var (native, session) = await OpenAsync();
        native.SeedTask(Mount, TaskName, TaskId, "Tasks");

        await Apply(session, Remove(TaskName));

        var deletes = native.Calls.Where(c => c.StartsWith("deleteKey:", StringComparison.Ordinal)).ToList();
        Assert.Equal($@"deleteKey:{Mount}\{TaskCache.TreePath(TaskName)}", deletes[^1]);
        Assert.Contains($@"deleteKey:{Mount}\{TaskCache.IdKeyedPath("Tasks", TaskId)}", deletes);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Every_id_keyed_subkey_is_swept_even_though_most_will_be_absent()
    {
        var (native, session) = await OpenAsync();
        native.SeedTask(Mount, TaskName, TaskId, "Tasks");

        await Apply(session, Remove(TaskName));

        foreach (var subkey in TaskCache.IdKeyedSubkeys)
        {
            Assert.Contains($@"deleteKey:{Mount}\{TaskCache.IdKeyedPath(subkey, TaskId)}", native.Calls);
        }

        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_task_that_was_never_registered_reports_no_target()
    {
        // The case docs/catalog-gaps.md section 3.1 predicts for Compatibility Appraiser on 26200.
        var (native, session) = await OpenAsync();

        Assert.Equal(ActionStatus.NoTarget, await Apply(session, Remove(TaskName)));
        Assert.DoesNotContain(native.Calls, c => c.StartsWith("deleteKey:", StringComparison.Ordinal));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Removing_the_same_task_twice_reports_applied_then_no_target()
    {
        var (native, session) = await OpenAsync();
        native.SeedTask(Mount, TaskName, TaskId, "Tasks");

        Assert.Equal(ActionStatus.Applied, await Apply(session, Remove(TaskName)));
        Assert.Equal(ActionStatus.NoTarget, await Apply(session, Remove(TaskName)));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_tree_node_with_no_id_is_refused_rather_than_half_deleted()
    {
        var (native, session) = await OpenAsync();
        native.Seed($@"{Mount}\{TaskCache.TreePath(TaskName)}");

        var ex = await Assert.ThrowsAsync<RegistryActionException>(() => Apply(session, Remove(TaskName)));

        Assert.Contains("Refusing to delete half of it", ex.Message, StringComparison.Ordinal);
        Assert.True(native.KeyExists($@"{Mount}\{TaskCache.TreePath(TaskName)}"));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_task_action_needs_no_hive_because_the_registration_is_always_in_software()
    {
        var (native, session) = await OpenAsync();
        native.SeedTask(Mount, TaskName, TaskId, "Tasks");

        // ActionValidator does not require 'hive' for removeScheduledTask, so the engine must not
        // either. This is the contract note in the findings: Core has to route the action to a
        // session that loaded SOFTWARE.
        Assert.Null(Remove(TaskName).Hive);
        Assert.Equal(ActionStatus.Applied, await Apply(session, Remove(TaskName)));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_task_action_naming_the_wrong_hive_is_rejected()
    {
        var native = new FakeNativeRegistry();
        var registry = new OfflineRegistry(native, HiveUnloadPolicy.Immediate());
        await using var session = await registry.OpenAsync(
            @"C:\mount",
            [RegistryHive.Software, RegistryHive.System],
            TestContext.Current.CancellationToken);

        var action = Remove(TaskName) with { Hive = RegistryHive.System };

        var ex = await Assert.ThrowsAsync<RegistryActionException>(() => Apply(session, action));
        Assert.Contains("Software hive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_action_against_a_session_without_the_software_hive_throws()
    {
        var native = new FakeNativeRegistry();
        var registry = new OfflineRegistry(native, HiveUnloadPolicy.Immediate());
        await using var session = await registry.OpenAsync(
            @"C:\mount", [RegistryHive.System], TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RegistryActionException>(() => Apply(session, Remove(TaskName)));
        Assert.Contains("Software", ex.Message, StringComparison.Ordinal);
    }
}
