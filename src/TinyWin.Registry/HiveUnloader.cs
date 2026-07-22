using TinyWin.Registry.Interop;

namespace TinyWin.Registry;

/// <summary>
/// The retry loop that decides whether a hive is stuck.
/// </summary>
/// <remarks>
/// Shared by <see cref="HiveSession"/> (normal teardown) and
/// <see cref="OfflineRegistry.UnloadStrandedHivesAsync"/> (crash recovery) so both get the same
/// forced-finalization behaviour — a stranded hive from last run is stuck for exactly the same
/// reasons as one from this run.
/// </remarks>
internal static class HiveUnloader
{
    /// <summary>
    /// Attempts to unload <paramref name="mountName"/>. Returns null on success, or the last
    /// failure if every attempt was exhausted. Never throws for an unload failure — the caller
    /// decides how to aggregate across hives — and is deliberately not cancellable, because
    /// abandoning an unload half way is precisely the outcome this code exists to prevent.
    /// </summary>
    public static async Task<Exception?> TryUnloadAsync(
        INativeRegistry native, string mountName, HiveUnloadPolicy policy)
    {
        var attempts = Math.Max(1, policy.MaxAttempts);
        var delay = policy.InitialDelay;
        Exception? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                native.UnloadHive(mountName);
                return null;
            }
            catch (RegistryOperationException ex)
            {
                last = ex;
            }

            if (attempt == attempts)
            {
                break;
            }

            // This project never holds a RegistryKey across a call, so anything still pinning the
            // hive from inside this process is a key awaiting finalization — most often one the
            // BCL created internally. Forcing the finalizer queue is the only lever we have, and
            // it is why the first retry usually succeeds. See docs/PLAN.md section 3.3.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }

            delay = Min(delay * policy.BackoffFactor, policy.MaxDelay);
        }

        return last;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
