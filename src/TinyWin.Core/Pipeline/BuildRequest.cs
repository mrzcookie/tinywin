using TinyWin.Core.Abstractions;

namespace TinyWin.Core.Pipeline;

public sealed record BuildRequest
{
    public required string SourceIsoPath { get; init; }
    public required string OutputIsoPath { get; init; }

    /// <summary>Edition index within install.wim to build from.</summary>
    public required int EditionIndex { get; init; }

    /// <summary>Component ids to remove. Already expanded from a preset, if one was used.</summary>
    public required IReadOnlyList<string> ComponentIds { get; init; }

    public UnattendOptions Unattend { get; init; } = new();

    /// <summary>Working directory. Needs roughly 25 GB free.</summary>
    public required string ScratchDirectory { get; init; }

    /// <summary>Recompress with recovery compression. Slow, and the bulk of the size win.</summary>
    public bool RecompressImage { get; init; } = true;

    /// <summary>
    /// Run StartComponentCleanup /ResetBase. Automatically skipped when the selection has already
    /// removed the component store.
    /// </summary>
    public bool CleanupComponentStore { get; init; } = true;

    /// <summary>Resume from the last checkpoint in the scratch directory rather than starting over.</summary>
    public bool Resume { get; init; }
}
