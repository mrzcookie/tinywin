namespace TinyWin.Registry;

/// <summary>
/// The layout of <c>SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache</c>, which is
/// where a scheduled task actually lives on an offline image.
/// </summary>
/// <remarks>
/// <para>
/// docs/catalog-gaps.md section 3.1: a 25H2 image ships only nine task definition files under
/// <c>Windows\System32\Tasks</c>. Everything else is materialised during setup from this
/// registration, so deleting task files offline mostly deletes nothing and the task reappears
/// after OOBE. Removing a task therefore means editing this key.
/// </para>
/// <para>
/// A registration is spread across two places. <c>Tree\&lt;task path&gt;</c> is the human-readable
/// node and carries an <c>Id</c> value holding the task's GUID; the GUID then keys an entry under
/// <c>Tasks</c>, and — depending on how the task is triggered — under one or more of the schedule
/// indexes below. Removing only one half leaves a broken registration, which is why this is
/// modelled as one action rather than composed from separate deletes.
/// </para>
/// <para>
/// <b>Unverified.</b> The subkey list below could not be checked against real media: the offline
/// hives need elevation to load, and <c>TaskCache</c> on this dev machine (also 26200) denies read
/// access to a non-elevated process. It is taken from the documented Task Scheduler registry
/// layout. <c>scripts/dump-offline-registry.ps1 -TaskCache</c> settles it in one elevated pass.
/// </para>
/// </remarks>
internal static class TaskCache
{
    /// <summary>Path to TaskCache within the SOFTWARE hive.</summary>
    public const string Root = @"Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";

    /// <summary>The value under a <c>Tree</c> node that holds the task's GUID.</summary>
    public const string IdValueName = "Id";

    /// <summary>
    /// Subkeys keyed by the task GUID. <c>Tasks</c> is the registration proper; the rest are
    /// trigger indexes a task appears in only if it has that kind of trigger, so a missing entry
    /// in those is normal rather than a no-op worth reporting.
    /// </summary>
    public static IReadOnlyList<string> IdKeyedSubkeys { get; } =
        ["Tasks", "Plain", "Logon", "Boot", "Maintenance"];

    public static string TreePath(string normalizedTaskName) => $@"{Root}\Tree\{normalizedTaskName}";

    public static string IdKeyedPath(string subkey, string id) => $@"{Root}\{subkey}\{id}";

    /// <summary>
    /// Normalises a catalog task path such as
    /// <c>\Microsoft\Windows\Application Experience\ProgramDataUpdater</c> into the relative form
    /// used under <c>Tree</c>.
    /// </summary>
    /// <remarks>
    /// Catalog task names are conventionally written with a leading backslash, matching how
    /// schtasks and the Task Scheduler UI display them. Under <c>Tree</c> that leading separator
    /// is not part of the key path.
    /// </remarks>
    public static string NormalizeTaskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new RegistryActionException("removeScheduledTask requires 'name'.");
        }

        var segments = name.Trim()
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            throw new RegistryActionException($"Scheduled task name resolved to nothing: '{name}'.");
        }

        if (segments.Contains(".."))
        {
            throw new RegistryActionException($"Scheduled task name must not traverse upwards, but was '{name}'.");
        }

        return string.Join('\\', segments);
    }
}
