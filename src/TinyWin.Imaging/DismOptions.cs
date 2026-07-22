namespace TinyWin.Imaging;

/// <summary>How verbose <c>dism.exe</c> should be in its own log file.</summary>
public enum DismLogLevel
{
    /// <summary>Do not pass <c>/LogLevel</c>; DISM uses its default.</summary>
    Default = 0,
    Errors = 1,
    ErrorsWarnings = 2,
    ErrorsWarningsInfo = 3,
    ErrorsWarningsInfoDebug = 4,
}

/// <summary>Knobs for <see cref="DismExeBackend"/> that do not belong on the interface.</summary>
public sealed record DismOptions
{
    public static DismOptions Default { get; } = new();

    /// <summary>
    /// Resolved through <c>PATH</c> by default. Note that on 64-bit Windows a 32-bit process gets
    /// the SysWOW64 DISM via redirection, which cannot service 64-bit images — TinyWin is x64 only,
    /// so this is a note for anyone tempted to change the RID rather than a supported scenario.
    /// </summary>
    public string ExecutablePath { get; init; } = "dism.exe";

    /// <summary>
    /// Passed as <c>/ScratchDir</c> to image-servicing commands. DISM otherwise scratches into
    /// <c>%TEMP%</c>, which is a poor choice when the temp volume is small.
    /// </summary>
    public string? ScratchDirectory { get; init; }

    /// <summary>Passed as <c>/LogPath</c>. DISM's own log is the first thing to read on a failure.</summary>
    public string? LogPath { get; init; }

    public DismLogLevel LogLevel { get; init; } = DismLogLevel.Default;
}
