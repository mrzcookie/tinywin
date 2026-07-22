using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder;

/// <summary>What Inspect learned from the user's source ISO.</summary>
public sealed record IsoInspection
{
    public required string IsoPath { get; init; }

    /// <summary>
    /// The boot geometry to reproduce. Cache this in state.json; it is what stops the rebuild from
    /// guessing a load size, which xorriso would accept silently and which would fail only at boot.
    /// </summary>
    public required IsoBootGeometry Geometry { get; init; }

    /// <summary>
    /// Anything about this media the report should mention — in particular, boot image paths that
    /// had to be assumed because the source ISO hides them.
    /// </summary>
    public required IReadOnlyList<string> Notes { get; init; }

    public required ElToritoReport PlainReport { get; init; }

    public required ElToritoReport AsMkisofsReport { get; init; }
}
