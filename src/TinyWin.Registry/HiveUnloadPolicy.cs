namespace TinyWin.Registry;

/// <summary>
/// How hard to try before declaring a hive stuck.
/// </summary>
/// <remarks>
/// Configurable mostly so tests can collapse the delays to zero, but the defaults are a real
/// judgement call: the transient causes of a failed unload (a finalizer that has not run, an
/// antivirus scanner walking the new mount point) clear in well under a second, and anything still
/// holding the hive after ~3 seconds is not going to let go. Waiting longer would only delay the
/// error the user needs to see.
/// </remarks>
public sealed record HiveUnloadPolicy
{
    public static HiveUnloadPolicy Default { get; } = new();

    /// <summary>Total unload attempts, including the first. Must be at least 1.</summary>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>Delay before the second attempt; doubles from there up to <see cref="MaxDelay"/>.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public double BackoffFactor { get; init; } = 2.0;

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Zero delays, for tests that assert on retry behaviour without paying for it.</summary>
    public static HiveUnloadPolicy Immediate(int maxAttempts = 6) => new()
    {
        MaxAttempts = maxAttempts,
        InitialDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero,
    };
}
