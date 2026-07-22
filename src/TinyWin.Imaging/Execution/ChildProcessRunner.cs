using System.Diagnostics;
using TinyWin.Imaging.Dism;

namespace TinyWin.Imaging.Execution;

/// <summary>
/// The real <see cref="IProcessRunner"/>: starts <c>dism.exe</c> and streams its output.
/// </summary>
/// <remarks>
/// <para>Deliberately does <b>not</b> use <see cref="Process.OutputDataReceived"/>. That event
/// fires per line, and DISM's progress bar is not lines — it repaints one line with backspaces or
/// bare carriage returns and may never terminate it until the operation finishes. Line-based
/// reading would therefore surface the entire progress bar as a single event at the end, which is
/// no progress at all. Raw character reads into <see cref="DismOutputReader"/> is what makes live
/// progress possible.</para>
/// <para>This class is the only part of TinyWin.Imaging that touches a real process, and the only
/// part unit tests cannot cover on an unelevated machine. Everything decision-shaped is kept out of
/// it on purpose.</para>
/// </remarks>
public sealed class ChildProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(request.FileName, request.Arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var lines = new List<string>();
        var sync = new object();
        var reader = new DismOutputReader(
            line =>
            {
                lock (sync)
                {
                    lines.Add(line);
                }

                request.Log?.Report(line);
            },
            fraction => request.Progress?.Report(fraction));

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new DismException($"Could not start '{request.FileName}': {ex.Message}", ex);
        }

        // Both streams must be drained concurrently; letting either fill its pipe buffer deadlocks
        // a child that is still writing to the other.
        var stdout = PumpAsync(process.StandardOutput, reader, sync);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var cancelled = false;
        if (request.KillOnCancel)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                KillQuietly(process);

                // Reap the child before returning: leaving DISM half-alive is how a mount point ends
                // up in a state /Cleanup-Mountpoints cannot recover.
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        else
        {
            // Unmount path. Cancellation is intentionally not honoured — an interrupted commit is
            // the corrupted image this whole design exists to prevent.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await stdout.ConfigureAwait(false);
        reader.Complete();

        var errorText = await stderr.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(errorText))
        {
            lock (sync)
            {
                lines.AddRange(errorText.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0));
            }
        }

        if (cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new ProcessRunResult(process.ExitCode, lines, reader.SawPercentage);
    }

    private static async Task PumpAsync(StreamReader source, DismOutputReader reader, object sync)
    {
        var buffer = new char[1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            lock (sync)
            {
                reader.Append(buffer.AsSpan(0, read));
            }
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            // entireProcessTree matters: dism.exe spawns DismHost.exe, and killing only the parent
            // leaves the child holding the mount.
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the cancellation and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied or already terminating; the wait below settles it either way.
        }
    }
}
