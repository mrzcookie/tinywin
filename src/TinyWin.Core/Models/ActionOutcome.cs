namespace TinyWin.Core.Models;

/// <summary>
/// What actually happened when an action ran.
/// </summary>
/// <remarks>
/// <see cref="NoTarget"/> is the reason this type exists. A catalog entry whose package name has
/// drifted silently does nothing, and a tool that reports success for it is lying. Distinguishing
/// "removed" from "was not there" is what keeps the catalog honest across builds — see
/// docs/PLAN.md section 2.1.
/// </remarks>
public enum ActionStatus
{
    Applied,
    NoTarget,
    Skipped,
    Failed,
}

public sealed record ActionOutcome
{
    public required string ComponentId { get; init; }
    public required string Description { get; init; }
    public required ActionStatus Status { get; init; }
    public string? Detail { get; init; }
    public TimeSpan Duration { get; init; }

    public static ActionOutcome Applied(string componentId, string description, TimeSpan duration = default) =>
        new() { ComponentId = componentId, Description = description, Status = ActionStatus.Applied, Duration = duration };

    public static ActionOutcome NoTarget(string componentId, string description) =>
        new() { ComponentId = componentId, Description = description, Status = ActionStatus.NoTarget };

    public static ActionOutcome Failed(string componentId, string description, string detail) =>
        new() { ComponentId = componentId, Description = description, Status = ActionStatus.Failed, Detail = detail };
}
