using System.Diagnostics;
using System.Text;

namespace TinyWin.IsoBuilder;

/// <summary>What an external tool did.</summary>
internal sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.IsNullOrEmpty(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;

    /// <summary>
    /// xorriso writes its banner, progress and diagnostics to stderr and its report output to
    /// stdout, but which stream a given line lands on has changed between versions. Parsers look
    /// at both.
    /// </summary>
    public IEnumerable<string> AllLines =>
        CombinedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Runs xorriso, oscdimg and dism.exe.
/// </summary>
/// <remarks>
/// Cancellation kills the child process rather than merely abandoning it. A half-written ISO is
/// harmless — it is deleted by the caller — but an orphaned xorriso holding the output file open
/// would block the retry, so the wait for exit is not skipped.
/// </remarks>
internal static class ToolProcess
{
    public static async Task<ToolResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => Capture(stdout, e.Data, onLine);
        process.ErrorDataReceived += (_, e) => Capture(stderr, e.Data, onLine);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new IsoBuilderException($"Could not start '{executablePath}': {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ToolResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void Capture(StringBuilder sink, string? line, Action<string>? onLine)
    {
        if (line is null)
        {
            return;
        }

        lock (sink)
        {
            sink.AppendLine(line);
        }

        onLine?.Invoke(line);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exiting on its own; nothing useful to do.
        }
    }
}
