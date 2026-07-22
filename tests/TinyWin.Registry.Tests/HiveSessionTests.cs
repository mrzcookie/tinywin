using System.Text.Json;
using TinyWin.Catalog.Models;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Registry.Tests.Fakes;
using Win32ValueKind = Microsoft.Win32.RegistryValueKind;

namespace TinyWin.Registry.Tests;

public class HiveSessionTests
{
    private const string SoftwareMount = @"zTW-SOFTWARE\Policies\TinyWin";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(FakeNativeRegistry Native, IHiveSession Session)> OpenAsync(
        params RegistryHive[] hives)
    {
        var native = new FakeNativeRegistry();
        var registry = new OfflineRegistry(native, HiveUnloadPolicy.Immediate());
        var session = await registry.OpenAsync(
            @"C:\mount", hives.Length == 0 ? [RegistryHive.Software] : hives,
            TestContext.Current.CancellationToken);
        return (native, session);
    }

    private static Task<ActionStatus> Apply(
        IHiveSession session, string componentId, ComponentAction action, CancellationToken? token = null) =>
        session.ApplyAsync(componentId, action, token ?? TestContext.Current.CancellationToken);

    private static ComponentAction Set(string key, string? valueName, RegistryValueKind kind, string data) => new()
    {
        Type = ActionType.SetRegistry,
        Hive = RegistryHive.Software,
        Key = key,
        ValueName = valueName,
        Kind = kind,
        Data = Json(data),
    };

    private static ComponentAction Delete(string key, string? valueName = null) => new()
    {
        Type = ActionType.DeleteRegistryKey,
        Hive = RegistryHive.Software,
        Key = key,
        ValueName = valueName,
    };

    [Fact]
    public async Task Set_registry_creates_the_key_and_writes_the_converted_value()
    {
        var (native, session) = await OpenAsync();

        var status = await Apply(
            session,
            "tweak.test", Set(@"Policies\TinyWin", "Enabled", RegistryValueKind.Dword, "1"));

        Assert.Equal(ActionStatus.Applied, status);
        Assert.Equal((object)1, native.Read(SoftwareMount, "Enabled")!.Value.Data);
        Assert.Equal(Win32ValueKind.DWord, native.Read(SoftwareMount, "Enabled")!.Value.Kind);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Set_registry_with_no_value_name_writes_the_default_value()
    {
        var (native, session) = await OpenAsync();

        await Apply(session, "tweak.test", Set(@"Policies\TinyWin", null, RegistryValueKind.Sz, "\"hello\""));

        Assert.Equal("hello", native.Read(SoftwareMount, string.Empty)!.Value.Data);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Set_registry_normalizes_the_key_before_writing()
    {
        var (native, session) = await OpenAsync();

        await Apply(session, "tweak.test", Set("/Policies/TinyWin/", "X", RegistryValueKind.Dword, "1"));

        Assert.NotNull(native.Read(SoftwareMount, "X"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Delete_of_a_key_that_is_not_there_reports_no_target()
    {
        // The distinction that keeps the catalog honest — see docs/PLAN.md section 2.1.
        var (_, session) = await OpenAsync();

        Assert.Equal(ActionStatus.NoTarget, await Apply(session, "app.test", Delete(@"Gone\Missing")));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Delete_of_a_key_that_exists_reports_applied()
    {
        var (native, session) = await OpenAsync();
        native.Seed(SoftwareMount);

        Assert.Equal(ActionStatus.Applied, await Apply(session, "app.test", Delete(@"Policies\TinyWin")));
        Assert.False(native.KeyExists(SoftwareMount));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Deleting_the_same_key_twice_reports_applied_then_no_target()
    {
        var (native, session) = await OpenAsync();
        native.Seed(SoftwareMount);

        Assert.Equal(ActionStatus.Applied, await Apply(session, "app.test", Delete(@"Policies\TinyWin")));
        Assert.Equal(ActionStatus.NoTarget, await Apply(session, "app.test", Delete(@"Policies\TinyWin")));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Delete_of_a_value_reports_applied_when_present_and_no_target_when_not()
    {
        var (native, session) = await OpenAsync();
        native.Seed(SoftwareMount, "Enabled");

        Assert.Equal(ActionStatus.Applied, await Apply(session, "app.test", Delete(@"Policies\TinyWin", "Enabled")));
        Assert.Equal(ActionStatus.NoTarget, await Apply(session, "app.test", Delete(@"Policies\TinyWin", "Enabled")));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Delete_of_a_value_under_a_missing_key_reports_no_target()
    {
        var (_, session) = await OpenAsync();

        Assert.Equal(ActionStatus.NoTarget, await Apply(session, "app.test", Delete(@"Gone", "Enabled")));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task An_action_against_a_hive_this_session_did_not_load_throws()
    {
        var (_, session) = await OpenAsync(RegistryHive.Software);

        var action = Set(@"Policies\TinyWin", "X", RegistryValueKind.Dword, "1") with
        {
            Hive = RegistryHive.System,
        };

        var ex = await Assert.ThrowsAsync<RegistryActionException>(() => Apply(session, "tweak.test", action));
        Assert.Contains("System", ex.Message, StringComparison.Ordinal);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_non_registry_action_throws_rather_than_silently_doing_nothing()
    {
        var (_, session) = await OpenAsync();
        var action = new ComponentAction { Type = ActionType.RemoveCapability, Name = "Browser.Internet.Explorer" };

        var ex = await Assert.ThrowsAsync<RegistryActionException>(() => Apply(session, "app.test", action));
        Assert.Contains("RemoveCapability", ex.Message, StringComparison.Ordinal);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_malformed_key_names_the_component_that_owns_it()
    {
        var (_, session) = await OpenAsync();
        var action = Set(@"HKLM\SOFTWARE\Microsoft", "X", RegistryValueKind.Dword, "1");

        var ex = await Assert.ThrowsAsync<RegistryActionException>(() => Apply(session, "tweak.bad", action));
        Assert.Contains("tweak.bad", ex.Message, StringComparison.Ordinal);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Apply_after_dispose_throws()
    {
        var (_, session) = await OpenAsync();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => Apply(session, "tweak.test", Set("Policies", "X", RegistryValueKind.Dword, "1")));
    }

    [Fact]
    public async Task Apply_honours_cancellation()
    {
        var (_, session) = await OpenAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Apply(session, "tweak.test", Set("Policies", "X", RegistryValueKind.Dword, "1"), cts.Token));

        await session.DisposeAsync();
    }
}
