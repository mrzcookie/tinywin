using System.Globalization;
using System.Text.RegularExpressions;
using TinyWin.Core.Abstractions;

namespace TinyWin.IsoBuilder;

/// <summary>
/// <see cref="IIsoBuilder"/> over xorriso (primary) and ADK oscdimg (fallback).
/// </summary>
/// <remarks>
/// <para>
/// The two backends are not interchangeable and the differences are load-bearing:
/// </para>
/// <list type="bullet">
/// <item>xorriso writes ISO 9660 level 3 + Joliet and cannot write UDF at all. It needs the boot
/// load sizes read from the source ISO, because it accepts a wrong one without complaint.</item>
/// <item>oscdimg writes UDF 1.02, exactly like Microsoft's own media, and derives load sizes from
/// the boot images itself. It cannot be redistributed, so it is only used when an ADK is
/// installed.</item>
/// </list>
/// <para>
/// Extraction never uses either: a Windows 11 ISO keeps its real content in the UDF tree, which
/// libisofs does not implement, so the image is mounted and copied through Windows' own reader.
/// </para>
/// </remarks>
public sealed partial class IsoBuilderService : IIsoBuilder
{
    private readonly IsoBuilderOptions _options;

    public IsoBuilderService()
        : this(new IsoBuilderOptions())
    {
    }

    public IsoBuilderService(IsoBuilderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Fired for every non-fatal observation, so the build report can surface it.</summary>
    public event EventHandler<string>? Diagnostic;

    public async Task<IReadOnlyList<IsoBackendAvailability>> ProbeBackendsAsync(
        CancellationToken cancellationToken = default)
    {
        return
        [
            await ProbeXorrisoAsync(cancellationToken).ConfigureAwait(false),
            ProbeOscdimg(),
        ];
    }

    /// <summary>
    /// Reads the boot geometry to reproduce from the user's source ISO. Run this during Inspect
    /// (docs/PLAN.md section 2.2 stage 2), cache it in state.json, and set it on
    /// <see cref="IsoBuildRequest.BootGeometry"/>.
    /// </summary>
    /// <remarks>
    /// This is the supported way to fill <see cref="IsoBuildRequest.BootGeometry"/>. Building
    /// without it falls back to <see cref="IsoDefaults"/> and warns, because xorriso accepts a
    /// wrong <c>-boot-load-size</c> silently and the media then fails only at boot.
    /// </remarks>
    // Nullable to match IIsoBuilder: the contract lets an implementation report "no readable boot
    // catalog" so the caller can fall back to defaults rather than fail the build. This
    // implementation always produces a geometry (InspectAsync falls back internally and records
    // the assumption), so it never actually returns null.
    public async Task<IsoBootGeometry?> ReadBootGeometryAsync(
        string isoPath,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(isoPath, cancellationToken).ConfigureAwait(false);
        return inspection.Geometry;
    }

    /// <summary>
    /// The full Inspect result: geometry plus the raw reports and any assumptions that had to be
    /// made about this particular media.
    /// </summary>
    /// <remarks>
    /// <see cref="IIsoBuilder"/> has no seam for Inspect-time capture, so callers reach this
    /// through the concrete type. See docs/findings/iso-builder.md.
    /// </remarks>
    public async Task<IsoInspection> InspectAsync(
        string isoPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);

        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"Source ISO '{isoPath}' does not exist.", isoPath);
        }

        var xorriso = RequireXorriso();

        var plainResult = await RunXorrisoAsync(
            xorriso,
            XorrisoCommandLine.ReportElToritoArguments(isoPath, ElToritoReportFormat.Plain),
            cancellationToken).ConfigureAwait(false);

        var mkisofsResult = await RunXorrisoAsync(
            xorriso,
            XorrisoCommandLine.ReportElToritoArguments(isoPath, ElToritoReportFormat.AsMkisofs),
            cancellationToken).ConfigureAwait(false);

        var plain = ElToritoReportParser.ParsePlain(plainResult.CombinedOutput);
        var asMkisofs = ElToritoReportParser.ParseAsMkisofs(mkisofsResult.CombinedOutput);

        var notes = new List<string>();
        var geometry = BuildGeometry(isoPath, plain, asMkisofs, notes);

        foreach (var note in notes)
        {
            Diagnostic?.Invoke(this, note);
        }

        return new IsoInspection
        {
            IsoPath = Path.GetFullPath(isoPath),
            Geometry = geometry,
            Notes = notes,
            PlainReport = plain,
            AsMkisofsReport = asMkisofs,
        };
    }

    public async Task BuildAsync(
        IsoBuildRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Directory.Exists(request.SourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Staged ISO tree '{request.SourceDirectory}' does not exist.");
        }

        var backend = SelectBackend();
        progress?.Report(0);

        if (request.SplitOversizeImage)
        {
            var split = await WimSplitter
                .SplitIfOversizeAsync(request.SourceDirectory, _options.DismPath, cancellationToken)
                .ConfigureAwait(false);

            Report(split.Performed
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"install.wim ({split.OriginalBytes} bytes) was split into {split.ProducedFiles.Count} .swm part(s).")
                : $"install.wim split requested but not performed: {split.SkipReason}");
        }

        progress?.Report(0.05);

        var preflight = IsoPreflight.Inspect(request.SourceDirectory, request.VolumeLabel);
        IsoPreflight.ThrowIfUnbuildable(preflight);

        foreach (var warning in preflight.Warnings)
        {
            Report(warning);
        }

        if (preflight.RequiresMultiExtent && backend == IsoBackendKind.Xorriso)
        {
            Report(
                $"'{preflight.LargestFilePath}' is " +
                $"{preflight.LargestFileBytes.ToString(CultureInfo.InvariantCulture)} bytes and needs " +
                "ISO 9660 level 3 multi-extent. Windows' cdfs.sys reads that correctly; WinPE's " +
                "boot-time reader has not been proven to. Set SplitOversizeImage if Setup cannot " +
                "read the image.");
        }

        progress?.Report(0.1);

        PrepareOutputPath(request.OutputIsoPath);

        try
        {
            switch (backend)
            {
                case IsoBackendKind.Xorriso:
                    await BuildWithXorrisoAsync(request, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case IsoBackendKind.Oscdimg:
                    await BuildWithOscdimgAsync(request, progress, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new IsoBuilderException($"Unknown ISO backend '{backend}'.");
            }
        }
        catch
        {
            // A partial ISO is worse than none: it looks like output and it is not bootable.
            DeleteIfExists(request.OutputIsoPath);
            throw;
        }

        progress?.Report(1);
    }

    public async Task ExtractAsync(
        string isoPath,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"Source ISO '{isoPath}' does not exist.", isoPath);
        }

        progress?.Report(0);

        using var mount = IsoImageMount.Attach(isoPath, cancellationToken);
        Report($"Mounted '{Path.GetFileName(isoPath)}' at {mount.RootPath}");

        var copied = await TreeCopier
            .CopyAsync(mount.RootPath, destinationDirectory, progress, cancellationToken)
            .ConfigureAwait(false);

        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"Extracted {copied.FileCount} file(s) in {copied.DirectoryCount} director(ies), {copied.TotalBytes} bytes."));

        progress?.Report(1);
    }

    /// <summary>
    /// Reads a finished image back and compares it with the tree it was built from.
    /// </summary>
    public async Task<IsoVerificationResult> VerifyAsync(
        string isoPath,
        string stagedTree,
        IsoBootGeometry expectedGeometry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedTree);
        ArgumentNullException.ThrowIfNull(expectedGeometry);

        var xorriso = RequireXorriso();

        var catalogResult = await RunXorrisoAsync(
            xorriso,
            XorrisoCommandLine.ReportElToritoArguments(isoPath, ElToritoReportFormat.Plain),
            cancellationToken).ConfigureAwait(false);

        var listingResult = await RunXorrisoAsync(
            xorriso,
            XorrisoCommandLine.ListAllFilesArguments(isoPath),
            cancellationToken).ConfigureAwait(false);

        var catalog = ElToritoReportParser.ParsePlain(catalogResult.CombinedOutput);
        var files = IsoContentListing.Parse(listingResult.CombinedOutput);

        return IsoVerification.Compare(files, stagedTree, catalog, expectedGeometry);
    }

    private async Task BuildWithXorrisoAsync(
        IsoBuildRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var geometry = request.BootGeometry ?? DefaultGeometryFor(request);
        var xorriso = RequireXorriso();
        var arguments = XorrisoCommandLine.BuildArguments(request, geometry);

        var result = await ToolProcess.RunAsync(
            xorriso,
            arguments,
            line => ReportWriteProgress(line, progress),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new IsoBuilderException(
                $"xorriso exited with code {result.ExitCode}:" + Environment.NewLine + result.CombinedOutput);
        }

        if (!File.Exists(request.OutputIsoPath))
        {
            throw new IsoBuilderException(
                $"xorriso reported success but '{request.OutputIsoPath}' was not created.");
        }

        progress?.Report(0.9);

        if (!_options.VerifyAfterBuild)
        {
            Report("Post-build verification was disabled; the image was not read back.");
            return;
        }

        var verification = await VerifyAsync(
            request.OutputIsoPath, request.SourceDirectory, geometry, cancellationToken).ConfigureAwait(false);

        foreach (var warning in verification.Warnings)
        {
            Report(warning);
        }

        if (!verification.Passed)
        {
            throw new IsoBuilderException(
                "The finished image does not match the staged tree:" +
                Environment.NewLine + verification.Describe());
        }

        Report(verification.Describe());
    }

    private async Task BuildWithOscdimgAsync(
        IsoBuildRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var oscdimg = BackendLocator.FindOscdimg(_options.OscdimgPath)
            ?? throw new IsoBuilderException(
                "oscdimg.exe was not found. Install the Windows ADK Deployment Tools, or use the " +
                "bundled xorriso backend.");

        var effective = OscdimgCommandLine.ApplyGeometry(request, request.BootGeometry);

        var result = await ToolProcess.RunAsync(
            oscdimg,
            OscdimgCommandLine.BuildArguments(effective),
            line => ReportWriteProgress(line, progress),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new IsoBuilderException(
                $"oscdimg exited with code {result.ExitCode}:" + Environment.NewLine + result.CombinedOutput);
        }

        if (!File.Exists(request.OutputIsoPath))
        {
            throw new IsoBuilderException(
                $"oscdimg reported success but '{request.OutputIsoPath}' was not created.");
        }

        progress?.Report(0.9);

        // oscdimg writes UDF, which libisofs cannot read, so the file-for-file comparison that
        // guards the xorriso path is not available here. Say so rather than implying it ran.
        Report("Built with oscdimg (UDF). Content verification is skipped: xorriso, which performs " +
               "the read-back, cannot read UDF.");
    }

    private async Task<IsoBackendAvailability> ProbeXorrisoAsync(CancellationToken cancellationToken)
    {
        var path = BackendLocator.FindXorriso(_options.XorrisoPath);

        if (path is null)
        {
            return new IsoBackendAvailability(
                IsoBackendKind.Xorriso,
                Available: false,
                ExecutablePath: null,
                Version: null,
                UnavailableReason: "xorriso.exe was not found next to the app or under tools/xorriso. " +
                                   "Run tools/fetch-xorriso.ps1 to download the vendored bundle.");
        }

        try
        {
            var result = await ToolProcess
                .RunAsync(path, XorrisoCommandLine.VersionArguments(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var version = ParseXorrisoVersion(result.CombinedOutput);

            return version is null
                ? new IsoBackendAvailability(
                    IsoBackendKind.Xorriso, false, path, null,
                    "xorriso.exe ran but did not report a version; the bundle may be incomplete.")
                : new IsoBackendAvailability(IsoBackendKind.Xorriso, true, path, version, null);
        }
        catch (IsoBuilderException ex)
        {
            return new IsoBackendAvailability(IsoBackendKind.Xorriso, false, path, null, ex.Message);
        }
    }

    private IsoBackendAvailability ProbeOscdimg()
    {
        var path = BackendLocator.FindOscdimg(_options.OscdimgPath);

        return path is null
            ? new IsoBackendAvailability(
                IsoBackendKind.Oscdimg,
                Available: false,
                ExecutablePath: null,
                Version: null,
                UnavailableReason: "No Windows ADK Deployment Tools install was found. oscdimg is not " +
                                   "redistributable, so TinyWin cannot supply it.")
            : new IsoBackendAvailability(
                IsoBackendKind.Oscdimg, true, path, BackendLocator.ReadFileVersion(path), null);
    }

    private IsoBackendKind SelectBackend()
    {
        var xorriso = BackendLocator.FindXorriso(_options.XorrisoPath);
        var oscdimg = BackendLocator.FindOscdimg(_options.OscdimgPath);

        if (_options.PreferredBackend == IsoBackendKind.Oscdimg)
        {
            if (oscdimg is not null)
            {
                return IsoBackendKind.Oscdimg;
            }

            if (xorriso is not null)
            {
                Report("oscdimg was selected but no ADK install was found; falling back to xorriso.");
                return IsoBackendKind.Xorriso;
            }
        }
        else
        {
            if (xorriso is not null)
            {
                return IsoBackendKind.Xorriso;
            }

            if (oscdimg is not null)
            {
                Report("The vendored xorriso bundle is missing; falling back to the detected ADK oscdimg.");
                return IsoBackendKind.Oscdimg;
            }
        }

        throw new IsoBuilderException(
            "No ISO backend is available. Run tools/fetch-xorriso.ps1 to install the bundled xorriso, " +
            "or install the Windows ADK Deployment Tools.");
    }

    private string RequireXorriso() =>
        BackendLocator.FindXorriso(_options.XorrisoPath)
        ?? throw new IsoBuilderException(
            "xorriso.exe was not found. Run tools/fetch-xorriso.ps1 to download the vendored bundle.");

    private static async Task<ToolResult> RunXorrisoAsync(
        string xorriso,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await ToolProcess
            .RunAsync(xorriso, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new IsoBuilderException(
                $"xorriso exited with code {result.ExitCode}:" + Environment.NewLine + result.CombinedOutput);
        }

        return result;
    }

    private static IsoBootGeometry BuildGeometry(
        string isoPath,
        ElToritoReport plain,
        ElToritoReport asMkisofs,
        List<string> notes)
    {
        var volumeId = plain.VolumeId ?? asMkisofs.VolumeId
            ?? throw new IsoBuilderException($"'{isoPath}' reported no volume id.");

        // plain is authoritative for geometry: it survives hidden boot images, which is how genuine
        // Microsoft media is laid out. as_mkisofs contributes paths where it can name them.
        var bios = plain.Bios ?? asMkisofs.Bios
            ?? throw new IsoBuilderException(
                $"'{isoPath}' has no BIOS (platform 0x00) El Torito entry. TinyWin reproduces the " +
                "source's dual BIOS+UEFI catalog and cannot invent one.");

        var uefi = plain.Uefi ?? asMkisofs.Uefi
            ?? throw new IsoBuilderException(
                $"'{isoPath}' has no UEFI (platform 0xEF) El Torito entry.");

        var biosPath = asMkisofs.Bios?.ImagePath ?? bios.ImagePath;
        var uefiPath = asMkisofs.Uefi?.ImagePath ?? uefi.ImagePath;

        if (biosPath is null)
        {
            biosPath = DefaultBiosBootImage;
            notes.Add(
                "The source ISO hides its BIOS boot image from the ISO 9660 tree, so its path could " +
                $"not be read; assuming '{DefaultBiosBootImage}'. This is normal for Microsoft media.");
        }

        if (uefiPath is null)
        {
            uefiPath = DefaultUefiBootImage;
            notes.Add(
                "The source ISO hides its UEFI boot image from the ISO 9660 tree, so its path could " +
                $"not be read; assuming '{DefaultUefiBootImage}'. This is normal for Microsoft media.");
        }

        return new IsoBootGeometry
        {
            VolumeId = volumeId,
            BiosBootImage = ToTreeRelative(biosPath),
            BiosLoadSize = bios.LoadSize,
            UefiBootImage = ToTreeRelative(uefiPath),
            UefiLoadSize = uefi.LoadSize,
        };
    }

    private const string DefaultBiosBootImage = IsoDefaults.BiosBootImage;
    private const string DefaultUefiBootImage = IsoDefaults.UefiBootImage;

    /// <summary>
    /// Falls back to the geometry observed on real 24H2/25H2 media when the source ISO was never
    /// inspected — and says so loudly.
    /// </summary>
    /// <remarks>
    /// The warning is the point. xorriso accepts a wrong load size without complaint and the
    /// resulting media fails only at boot, so an assumed value that goes unmentioned is precisely
    /// the silent no-op CLAUDE.md forbids. The caller is told to run
    /// <see cref="ReadBootGeometryAsync"/> instead.
    /// </remarks>
    private IsoBootGeometry DefaultGeometryFor(IsoBuildRequest request)
    {
        var geometry = IsoDefaults.WindowsMediaGeometry(request.VolumeLabel) with
        {
            BiosBootImage = request.BiosBootImage,
            UefiBootImage = request.UefiBootImage,
        };

        Report(
            "IsoBuildRequest.BootGeometry was null, so the boot load sizes were not read from the " +
            $"source ISO. Falling back to the values observed on 24H2/25H2 media " +
            $"(BIOS {IsoDefaults.BiosLoadSize.ToString(CultureInfo.InvariantCulture)}, " +
            $"UEFI {IsoDefaults.UefiLoadSize.ToString(CultureInfo.InvariantCulture)} sectors). " +
            "xorriso accepts a wrong load size silently and the media then fails only at boot — " +
            $"call {nameof(ReadBootGeometryAsync)} on the source ISO to read the real values.");

        return geometry;
    }

    private static string ToTreeRelative(string path) =>
        path.Trim().TrimStart('/', '\\').Replace('/', '\\');

    private static void PrepareOutputPath(string outputIsoPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputIsoPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DeleteIfExists(outputIsoPath);
    }

    private static void DeleteIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException)
        {
            // Held open by something else; the build will fail with a clearer message.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ReportWriteProgress(string line, IProgress<double>? progress)
    {
        if (progress is null)
        {
            return;
        }

        var match = PercentPattern().Match(line);
        if (!match.Success ||
            !double.TryParse(match.Groups["pct"].Value, CultureInfo.InvariantCulture, out var percent))
        {
            return;
        }

        // Writing occupies the 0.10-0.90 band; preflight and verification hold the ends.
        progress.Report(0.10 + (Math.Clamp(percent, 0, 100) / 100 * 0.80));
    }

    private static string? ParseXorrisoVersion(string output)
    {
        var match = VersionPattern().Match(output);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private void Report(string message) => Diagnostic?.Invoke(this, message);

    [GeneratedRegex(@"xorriso version\s*:\s*(?<version>[\d.]+)", RegexOptions.ExplicitCapture)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?<pct>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.ExplicitCapture)]
    private static partial Regex PercentPattern();
}
