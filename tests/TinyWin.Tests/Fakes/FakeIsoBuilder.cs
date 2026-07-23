using TinyWin.Core.Abstractions;

namespace TinyWin.Tests.Fakes;

/// <summary>
/// An <see cref="IIsoBuilder"/> that writes a believable staged tree and a believable ISO.
/// </summary>
/// <remarks>
/// It produces real files rather than recording calls, because the stages downstream of it do real
/// filesystem work — <c>InspectIsoStage</c> stats install.wim, <c>VerifyStage</c> refuses an
/// output that does not exist. A fake that only recorded calls would force those stages to be
/// stubbed out of any end-to-end test, which is exactly the coverage worth having.
/// </remarks>
public sealed class FakeIsoBuilder : IIsoBuilder
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls => _calls;

    /// <summary>Bytes written for each file in the staged tree.</summary>
    public int StagedFileSize { get; set; } = 4096;

    public bool BackendAvailable { get; set; } = true;

    public IsoBootGeometry? BootGeometry { get; set; } = new()
    {
        VolumeId = "CCCOMA_X64FRE_EN-US_DV9",
        BiosBootImage = @"boot\etfsboot.com",
        BiosLoadSize = 8,
        UefiBootImage = @"efi\microsoft\boot\efisys.bin",
        UefiLoadSize = 1,
    };

    /// <summary>Set to make <see cref="BuildAsync"/> throw, to exercise a late failure.</summary>
    public Exception? BuildFailure { get; set; }

    /// <summary>Runs on every call, so a test can cancel at a chosen point in the pipeline.</summary>
    public Action<string>? OnCall { get; set; }

    /// <summary>Ships install.esd instead of install.wim, to exercise the normalize stage.</summary>
    public bool ProduceEsd { get; set; }

    public Task<IReadOnlyList<IsoBackendAvailability>> ProbeBackendsAsync(
        CancellationToken cancellationToken = default)
    {
        Record("ProbeBackends()");

        return Task.FromResult<IReadOnlyList<IsoBackendAvailability>>(
        [
            new IsoBackendAvailability(
                IsoBackendKind.Xorriso,
                BackendAvailable,
                BackendAvailable ? @"tools\xorriso\xorriso.exe" : null,
                BackendAvailable ? "1.5.6" : null,
                BackendAvailable ? null : @"tools\xorriso\xorriso.exe is missing"),
            new IsoBackendAvailability(
                IsoBackendKind.Oscdimg, false, null, null, "No Windows ADK found"),
        ]);
    }

    public Task<IsoBootGeometry?> ReadBootGeometryAsync(
        string isoPath, CancellationToken cancellationToken = default)
    {
        Record($"ReadBootGeometry({isoPath})");
        return Task.FromResult(BootGeometry);
    }

    public async Task ExtractAsync(
        string isoPath, string destinationDirectory,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Record($"Extract({isoPath} -> {destinationDirectory})");
        cancellationToken.ThrowIfCancellationRequested();

        var sources = Path.Combine(destinationDirectory, "sources");
        Directory.CreateDirectory(sources);
        Directory.CreateDirectory(Path.Combine(destinationDirectory, "boot"));

        await WriteAsync(Path.Combine(sources, ProduceEsd ? "install.esd" : "install.wim")).ConfigureAwait(false);
        await WriteAsync(Path.Combine(sources, "boot.wim")).ConfigureAwait(false);
        await WriteAsync(Path.Combine(destinationDirectory, "boot", "etfsboot.com")).ConfigureAwait(false);
        await WriteAsync(Path.Combine(destinationDirectory, "setup.exe")).ConfigureAwait(false);

        progress?.Report(1.0);
    }

    public async Task BuildAsync(
        IsoBuildRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Record($"Build({request.SourceDirectory} -> {request.OutputIsoPath})");
        cancellationToken.ThrowIfCancellationRequested();

        if (BuildFailure is not null)
        {
            throw BuildFailure;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputIsoPath))!);
        await WriteAsync(request.OutputIsoPath).ConfigureAwait(false);

        progress?.Report(1.0);
    }

    private Task WriteAsync(string path) =>
        File.WriteAllBytesAsync(path, new byte[StagedFileSize]);

    private void Record(string call)
    {
        _calls.Add(call);
        OnCall?.Invoke(call);
    }
}

/// <summary>An <see cref="IUnattendGenerator"/> that produces something well-formed and short.</summary>
public sealed class FakeUnattendGenerator : IUnattendGenerator
{
    public int Generated { get; private set; }

    public string Generate(UnattendOptions options, string architecture)
    {
        Generated++;
        return $"""<?xml version="1.0"?><unattend arch="{architecture}" />""";
    }
}
