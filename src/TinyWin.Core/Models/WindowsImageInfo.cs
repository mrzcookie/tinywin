namespace TinyWin.Core.Models;

/// <summary>One selectable edition inside an install.wim / install.esd.</summary>
public sealed record ImageEdition
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string EditionId { get; init; }
    public required string Architecture { get; init; }
    public required Version Version { get; init; }
    public required long SizeBytes { get; init; }
    public string? DefaultLanguage { get; init; }

    public int Build => Version.Build;
}

/// <summary>What we learned by inspecting the source ISO, before any modification.</summary>
public sealed record WindowsImageInfo
{
    public required string SourceIsoPath { get; init; }

    /// <summary>True when the source ships install.esd and must be exported to WIM first.</summary>
    public required bool IsEsd { get; init; }

    public required IReadOnlyList<ImageEdition> Editions { get; init; }

    public required long TotalSizeBytes { get; init; }

    /// <summary>
    /// Build number used for catalog <c>appliesTo</c> matching. Taken from the selected edition.
    /// </summary>
    public int Build => Editions.Count > 0 ? Editions[0].Build : 0;
}

/// <summary>How the app judges media it has been handed. See docs/PLAN.md section 1.</summary>
public enum MediaSupport
{
    /// <summary>24H2 / 25H2. Catalog validated against this branch.</summary>
    Supported,

    /// <summary>A newer branch such as 26H1. Allowed, but warn that actions may no-op.</summary>
    Unverified,

    /// <summary>23H2 or older, past end of updates. Refused at inspect time.</summary>
    Unsupported,
}

public static class MediaSupportPolicy
{
    public const int MinimumSupportedBuild = 26100;
    public const int MaximumSupportedBuild = 26299;

    public static MediaSupport Classify(int build) => build switch
    {
        >= MinimumSupportedBuild and <= MaximumSupportedBuild => MediaSupport.Supported,
        > MaximumSupportedBuild => MediaSupport.Unverified,
        _ => MediaSupport.Unsupported,
    };
}
