namespace TinyWin.Imaging.Execution;

/// <summary>One <c>dism.exe</c> invocation.</summary>
/// <param name="FileName">The executable, usually resolved through <c>PATH</c>.</param>
/// <param name="Arguments">Built by <see cref="Dism.DismCommandLine"/>.</param>
/// <param name="Progress">Receives DISM's percentages, when it emits any.</param>
/// <param name="Log">
/// Receives each line DISM prints, as it prints it. Two jobs: it feeds the Build page's live log,
/// and it is the fallback signal of life when DISM emits no percentages at all.
/// </param>
/// <param name="KillOnCancel">
/// Whether cancelling the token should kill the child. False for the unmount commands, where an
/// interrupted process is precisely the half-written state the pipeline exists to avoid.
/// </param>
public sealed record ProcessRunRequest(
    string FileName,
    string Arguments,
    IProgress<double>? Progress = null,
    IProgress<string>? Log = null,
    bool KillOnCancel = true);

/// <param name="ExitCode">Win32 code or HRESULT — see <see cref="Dism.DismExitCode"/>.</param>
/// <param name="OutputLines">stdout and stderr, progress-bar repaints removed.</param>
/// <param name="SawPercentage">
/// Whether DISM reported any real percentage. False means the caller must present the operation as
/// indeterminate; reporting it as 0% would be a lie, and reporting nothing would look hung.
/// </param>
public sealed record ProcessRunResult(int ExitCode, IReadOnlyList<string> OutputLines, bool SawPercentage)
{
    public string Output => string.Join(Environment.NewLine, OutputLines);
}

/// <summary>
/// Starts child processes.
/// </summary>
/// <remarks>
/// The seam that makes <see cref="DismExeBackend"/> testable. Everything the backend does is
/// "build a command line, run it, parse the output", and only the middle step needs a real machine
/// — so tests substitute a runner that replays captured DISM output and assert on both the command
/// lines produced and the decisions made from the output.
/// </remarks>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);
}
