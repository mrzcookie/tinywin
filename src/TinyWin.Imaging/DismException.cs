using TinyWin.Imaging.Dism;

namespace TinyWin.Imaging;

/// <summary>A <c>dism.exe</c> invocation that failed.</summary>
public class DismException : Exception
{
    public DismException()
    {
    }

    public DismException(string message)
        : base(message)
    {
    }

    public DismException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DismException(int exitCode, string commandLine, string output)
        : base(BuildMessage(exitCode, commandLine))
    {
        ExitCode = exitCode;
        Kind = DismExitCode.Classify(exitCode);
        CommandLine = commandLine;
        Output = output;
    }

    public int ExitCode { get; }

    public DismErrorKind Kind { get; }

    /// <summary>The arguments passed to <c>dism.exe</c>. Reproducing a failure by hand needs this.</summary>
    public string CommandLine { get; } = string.Empty;

    /// <summary>Everything DISM wrote to stdout and stderr.</summary>
    public string Output { get; } = string.Empty;

    /// <summary>
    /// Creates the most specific exception type for <paramref name="exitCode"/>.
    /// </summary>
    public static DismException ForExitCode(int exitCode, string commandLine, string output) =>
        DismExitCode.Classify(exitCode) == DismErrorKind.ElevationRequired
            ? new DismElevationRequiredException(exitCode, commandLine, output)
            : new DismException(exitCode, commandLine, output);

    private static string BuildMessage(int exitCode, string commandLine) =>
        $"{DismExitCode.Describe(exitCode)}{Environment.NewLine}Command: dism.exe {commandLine}";
}

/// <summary>
/// DISM refused because the process is not elevated (740).
/// </summary>
/// <remarks>
/// A distinct type because this is the one DISM failure with a specific, actionable remedy — the UI
/// should offer to relaunch elevated rather than show a generic build-failed dialog. It is also
/// entirely expected: DISM performs its elevation check before any work, so even
/// <c>/Get-WimInfo</c> hits it (docs/spikes/dism-backend.md §5).
/// </remarks>
public sealed class DismElevationRequiredException : DismException
{
    public DismElevationRequiredException()
    {
    }

    public DismElevationRequiredException(string message)
        : base(message)
    {
    }

    public DismElevationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DismElevationRequiredException(int exitCode, string commandLine, string output)
        : base(exitCode, commandLine, output)
    {
    }
}
