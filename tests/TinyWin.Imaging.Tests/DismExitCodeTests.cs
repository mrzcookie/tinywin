using TinyWin.Imaging;
using TinyWin.Imaging.Dism;

namespace TinyWin.Imaging.Tests;

public class DismExitCodeTests
{
    [Fact]
    public void Zero_is_success()
    {
        Assert.True(DismExitCode.IsSuccess(0));
        Assert.Equal(DismErrorKind.Success, DismExitCode.Classify(0));
    }

    /// <summary>
    /// 3010 is ERROR_SUCCESS_REBOOT_REQUIRED. Treating it as a failure is the classic bug in
    /// wrappers around DISM, and offline servicing does not need the reboot anyway.
    /// </summary>
    [Fact]
    public void Reboot_required_is_a_success()
    {
        Assert.True(DismExitCode.IsSuccess(3010));
        Assert.Equal(DismErrorKind.RebootRequired, DismExitCode.Classify(3010));
    }

    [Fact]
    public void Error_740_is_elevation_required()
    {
        Assert.Equal(DismErrorKind.ElevationRequired, DismExitCode.Classify(740));
        Assert.False(DismExitCode.IsSuccess(740));
    }

    /// <summary>
    /// The requirement, stated directly: 740 must produce something a user can act on. A message
    /// that only repeats the number is the failure this asserts against.
    /// </summary>
    [Fact]
    public void Error_740_explains_what_to_do_rather_than_repeating_the_number()
    {
        var message = DismExitCode.Describe(740);

        Assert.Contains("Administrator", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("740", message, StringComparison.Ordinal);
        Assert.True(message.Length > 80, "The 740 message must actually explain the remedy.");
    }

    [Fact]
    public void The_hresult_form_of_elevation_required_maps_too()
    {
        // DismInitialize returns 0x800702E4 for the same condition — see docs/spikes/dism-backend.md §5.
        Assert.Equal(DismErrorKind.ElevationRequired, DismExitCode.Classify(unchecked((int)0x800702E4)));
    }

    [Theory]
    [InlineData(2, DismErrorKind.FileNotFound)]
    [InlineData(5, DismErrorKind.AccessDenied)]
    [InlineData(32, DismErrorKind.MountPointBusy)]
    [InlineData(87, DismErrorKind.InvalidArgument)]
    [InlineData(112, DismErrorKind.DiskFull)]
    [InlineData(145, DismErrorKind.MountDirectoryNotEmpty)]
    [InlineData(1223, DismErrorKind.Cancelled)]
    [InlineData(1168, DismErrorKind.TargetNotFound)]
    public void Common_win32_codes_are_classified(int code, DismErrorKind expected)
    {
        Assert.Equal(expected, DismExitCode.Classify(code));
    }

    [Theory]
    [InlineData(0x80070002u, DismErrorKind.FileNotFound)]
    [InlineData(0x800F080Cu, DismErrorKind.TargetNotFound)]
    [InlineData(0x80073CF1u, DismErrorKind.TargetNotFound)]
    [InlineData(0x800F081Fu, DismErrorKind.SourceMissing)]
    [InlineData(0xC1420127u, DismErrorKind.ImageAlreadyMounted)]
    public void Common_hresults_are_classified(uint code, DismErrorKind expected)
    {
        Assert.Equal(expected, DismExitCode.Classify(unchecked((int)code)));
    }

    /// <summary>
    /// The codes that mean "it was not there" drive <c>ActionStatus.NoTarget</c>, so they must be
    /// distinguishable from real failures.
    /// </summary>
    [Fact]
    public void Missing_target_codes_are_distinguished_from_failures()
    {
        Assert.True(DismExitCode.IsMissingTarget(unchecked((int)0x800F080C)));
        Assert.True(DismExitCode.IsMissingTarget(1168));

        Assert.False(DismExitCode.IsMissingTarget(740));
        Assert.False(DismExitCode.IsMissingTarget(5));
        Assert.False(DismExitCode.IsMissingTarget(112));
    }

    /// <summary>An unknown code must still be reportable, not swallowed.</summary>
    [Fact]
    public void Unknown_codes_keep_the_raw_value_in_both_bases()
    {
        var message = DismExitCode.Describe(unchecked((int)0xDEADBEEF));

        Assert.Equal(DismErrorKind.Unknown, DismExitCode.Classify(unchecked((int)0xDEADBEEF)));
        Assert.Contains("0xDEADBEEF", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_kind_has_a_message()
    {
        foreach (var code in (int[])[0, 3010, 740, 2, 5, 32, 87, 112, 145, 1223, 1168, 12345])
        {
            Assert.False(string.IsNullOrWhiteSpace(DismExitCode.Describe(code)));
        }
    }

    [Fact]
    public void Elevation_failures_get_their_own_exception_type()
    {
        var elevation = DismException.ForExitCode(740, "/English /Get-WimInfo", Samples.Error740);
        var other = DismException.ForExitCode(5, "/English /Get-WimInfo", string.Empty);

        Assert.IsType<DismElevationRequiredException>(elevation);
        Assert.IsType<DismException>(other, exactMatch: true);
    }

    [Fact]
    public void The_exception_keeps_the_command_line_for_reproduction()
    {
        var exception = DismException.ForExitCode(5, "/English /Get-MountedWimInfo", "some output");

        Assert.Equal("/English /Get-MountedWimInfo", exception.CommandLine);
        Assert.Equal("some output", exception.Output);
        Assert.Equal(5, exception.ExitCode);
        Assert.Contains("/English /Get-MountedWimInfo", exception.Message, StringComparison.Ordinal);
    }
}
