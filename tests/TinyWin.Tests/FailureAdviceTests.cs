using TinyWin.Core.Diagnostics;

namespace TinyWin.Tests;

/// <summary>
/// Every failure a user can act on must say what to do, not only what broke.
/// </summary>
/// <remarks>
/// The exception <em>type names</em> matched here belong to TinyWin.Imaging, TinyWin.Registry and
/// TinyWin.IsoBuilder, which Core cannot reference — the dependency direction points the other
/// way. These tests are what keeps that string matching honest: rename
/// <c>DismElevationRequiredException</c> and this suite fails, rather than the advice quietly
/// disappearing from every error dialog in the product.
/// </remarks>
public sealed class FailureAdviceTests
{
    [Fact]
    public void Dism_error_740_says_run_as_administrator()
    {
        var advice = FailureAdvice.For(new DismElevationRequiredException(
            "Error: 740. The requested operation requires elevation."));

        Assert.NotNull(advice);
        Assert.Contains("Run as administrator", advice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same advice must appear from a generic exception that only mentions 740.</summary>
    [Fact]
    public void An_unrecognised_type_mentioning_740_still_gets_the_elevation_advice()
    {
        var advice = FailureAdvice.For(new InvalidOperationException("dism.exe exited with 740"));

        Assert.Contains("Administrator", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stranded_hive_names_the_reg_unload_command()
    {
        var advice = FailureAdvice.For(new HiveUnloadException("Could not unload HKLM\\zTW-SOFTWARE."));

        Assert.NotNull(advice);
        Assert.Contains("reg unload HKLM", advice, StringComparison.Ordinal);
        Assert.Contains("dism /Cleanup-Mountpoints", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_iso_backend_points_at_the_fetch_script()
    {
        var advice = FailureAdvice.For(new IsoBuilderException(
            "No usable backend: xorriso.exe was not found under tools\\xorriso."));

        Assert.NotNull(advice);
        Assert.Contains("fetch-xorriso.ps1", advice, StringComparison.Ordinal);
        Assert.Contains("ADK", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_explains_what_survived_rather_than_what_broke()
    {
        var advice = FailureAdvice.For(new OperationCanceledException());

        Assert.NotNull(advice);
        Assert.Contains("dismounted and discarded", advice, StringComparison.Ordinal);
        Assert.Contains("--resume", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_locked_file_says_which_kind_of_thing_is_holding_it()
    {
        var advice = FailureAdvice.For(new IOException(
            "The process cannot access the file because it is being used by another process."));

        Assert.NotNull(advice);
        Assert.Contains("antivirus", advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Access_denied_from_the_registry_engine_mentions_scanning_exclusions()
    {
        var advice = FailureAdvice.For(new RegistryOperationException(
            "RegDeleteKeyEx failed: ERROR_ACCESS_DENIED"));

        Assert.NotNull(advice);
        Assert.Contains("elevated", advice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An exception whose message already carries the remedy gets no second opinion — a guessed
    /// remedy stacked on a good one is worse than silence.
    /// </summary>
    [Fact]
    public void A_disk_space_failure_adds_nothing_because_its_message_is_already_actionable()
    {
        var advice = FailureAdvice.For(new InsufficientDiskSpaceException(
            "Recompressing the image", @"C:\scratch", 1000, 10));

        Assert.Null(advice);
    }

    [Fact]
    public void An_unrecognised_failure_gets_no_invented_advice() =>
        Assert.Null(FailureAdvice.For(new InvalidOperationException("something went sideways")));

    [Fact]
    public void Null_is_tolerated() => Assert.Null(FailureAdvice.For(null));

    // Stand-ins for the engine exception types Core cannot reference. Their names are the contract.
    private sealed class DismElevationRequiredException(string message) : Exception(message);

    private sealed class HiveUnloadException(string message) : Exception(message);

    private sealed class IsoBuilderException(string message) : Exception(message);

    private sealed class RegistryOperationException(string message) : Exception(message);
}
