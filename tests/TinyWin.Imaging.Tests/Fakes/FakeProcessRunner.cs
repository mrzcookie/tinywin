using System.Collections.Concurrent;
using TinyWin.Imaging.Execution;

namespace TinyWin.Imaging.Tests.Fakes;

/// <summary>
/// An <see cref="IProcessRunner"/> that replays canned DISM output.
/// </summary>
/// <remarks>
/// This is what lets <c>DismExeBackend</c> be tested at all on a machine that cannot run DISM. It
/// records every command line, so tests assert on both halves of the backend's job: the arguments
/// it builds, and the decisions it makes from the output it gets back.
/// </remarks>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(string Match, ProcessRunResult Result)> _responses = [];
    private readonly ConcurrentQueue<ProcessRunRequest> _requests = new();

    /// <summary>Every request, in order.</summary>
    public IReadOnlyList<ProcessRunRequest> Requests => [.. _requests];

    public IReadOnlyList<string> CommandLines => [.. _requests.Select(r => r.Arguments)];

    /// <summary>Set to throw from the next matching call, to exercise cancellation and failure paths.</summary>
    public Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>? Interceptor { get; set; }

    /// <summary>Result used when no response matches. Success with no output.</summary>
    public ProcessRunResult Fallback { get; set; } = new(0, [], SawPercentage: false);

    public FakeProcessRunner Respond(string argumentSubstring, string output, int exitCode = 0)
    {
        _responses.Add((
            argumentSubstring,
            new ProcessRunResult(exitCode, [.. output.Split('\n').Select(l => l.TrimEnd('\r'))], false)));
        return this;
    }

    public FakeProcessRunner RespondWithExitCode(string argumentSubstring, int exitCode)
    {
        _responses.Add((argumentSubstring, new ProcessRunResult(exitCode, [], false)));
        return this;
    }

    public int CountMatching(string argumentSubstring) =>
        _requests.Count(r => r.Arguments.Contains(argumentSubstring, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Pushes the canned output through the request's log sink, the way the real runner does as
    /// lines arrive. Without this the stage-progress fallback would never be exercised.
    /// </summary>
    private static ProcessRunResult Replay(ProcessRunRequest request, ProcessRunResult result)
    {
        if (request.Log is not null)
        {
            foreach (var line in result.OutputLines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                request.Log.Report(line);
            }
        }

        return result;
    }

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        _requests.Enqueue(request);

        if (Interceptor is not null)
        {
            return await Interceptor(request, cancellationToken).ConfigureAwait(false);
        }

        // Honour cancellation the way the real runner does, so cancellation tests are meaningful.
        if (request.KillOnCancel)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        foreach (var (match, result) in _responses)
        {
            if (request.Arguments.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                return Replay(request, result);
            }
        }

        return Replay(request, Fallback);
    }
}
