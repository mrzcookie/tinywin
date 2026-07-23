using System.Text.Json;
using TinyWin.Catalog.Models;
using TinyWin.Catalog.Resolution;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;

namespace TinyWin.Tests.Fakes;

/// <summary>
/// A whole build, on fakes, in a temp directory.
/// </summary>
/// <remarks>
/// Exists so the pipeline can be exercised above the unit level — the level where the failures
/// that matter live. Rollback, cancellation unwinding and resume are all properties of the stages
/// running <em>together</em>: every one of them is green in isolation and can still leave an image
/// mounted in combination. It builds through <see cref="BuildPipelineFactory"/> rather than a
/// hand-listed subset of stages, deliberately, so a stage added to the real pipeline is covered
/// here the day it lands.
/// </remarks>
public sealed class PipelineHarness : IDisposable
{
    public PipelineHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "tinywin-m6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        SourceIsoPath = Path.Combine(Root, "Win11_25H2.iso");
        File.WriteAllBytes(SourceIsoPath, new byte[64 * 1024]);

        OutputIsoPath = Path.Combine(Root, "out", "Win11_25H2-tiny.iso");
        ScratchDirectory = Path.Combine(Root, "scratch");

        Imaging.Editions.Add(new ImageEdition
        {
            Index = 1,
            Name = "Windows 11 Pro",
            Description = "Windows 11 Pro",
            EditionId = "Professional",
            Architecture = "amd64",
            Version = new Version(10, 0, 26200, 1),
            SizeBytes = 16L * 1024 * 1024 * 1024,
            DefaultLanguage = "en-US",
        });

        Imaging.PresentPackages.Add("Microsoft.XboxApp");
        Imaging.PresentPackages.Add("Microsoft.GamingApp");
    }

    public string Root { get; }

    public string SourceIsoPath { get; }

    public string OutputIsoPath { get; }

    public string ScratchDirectory { get; }

    public FakeImagingBackend Imaging { get; } = new();

    public FakeOfflineRegistry Registry { get; } = new();

    public FakeIsoBuilder IsoBuilder { get; } = new();

    public FakeUnattendGenerator Unattend { get; } = new();

    public FakeBuildEnvironment Environment { get; } = new();

    public List<BuildProgress> Progress { get; } = [];

    /// <summary>The checkpoint file the pipeline writes, for tests that inspect or corrupt it.</summary>
    public string CheckpointPath => Path.Combine(ScratchDirectory, "state.json");

    public BuildRequest Request(bool resume = false) => new()
    {
        SourceIsoPath = SourceIsoPath,
        OutputIsoPath = OutputIsoPath,
        EditionIndex = 1,
        ComponentIds = ["apps.xbox", "privacy.telemetry"],
        ScratchDirectory = ScratchDirectory,
        Resume = resume,
    };

    /// <summary>
    /// A plan with one appx bundle (two present, one absent), one file delete and one registry
    /// write — enough to exercise applied, no-target and the registry stage in one run.
    /// </summary>
    public static ResolvedPlan Plan() => new()
    {
        ComponentIds = ["apps.xbox", "privacy.telemetry"],
        ImageActions =
        [
            new ResolvedAction("apps.xbox", new ComponentAction
            {
                Type = ActionType.RemoveProvisionedAppx,
                Packages = ["Microsoft.XboxApp", "Microsoft.GamingApp", "Microsoft.NotHere"],
            }),
        ],
        RegistryActions =
        [
            new ResolvedAction("privacy.telemetry", new ComponentAction
            {
                Type = ActionType.SetRegistry,
                Hive = RegistryHive.Software,
                Key = @"Policies\Microsoft\Windows\DataCollection",
                ValueName = "AllowTelemetry",
                Kind = RegistryValueKind.Dword,
                Data = JsonDocument.Parse("0").RootElement,
            }),
        ],
        Warnings = [],
        EstimatedSavingsMb = 340,
        HighestRisk = RiskTier.Safe,
    };

    public BuildContext Context(bool resume = false) => new()
    {
        Request = Request(resume),
        Plan = Plan(),
    };

    public BuildPipeline Pipeline() => BuildPipelineFactory.Create(
        Imaging, Registry, IsoBuilder, Unattend, ScratchDirectory, Environment);

    public async Task<BuildReport> RunAsync(
        BuildContext context, CancellationToken cancellationToken = default)
    {
        // Progress<T> can deliver on the thread pool, so the sink needs a lock of its own.
        var sink = new Progress<BuildProgress>(p =>
        {
            lock (Progress)
            {
                Progress.Add(p);
            }
        });

        return await Pipeline().RunAsync(context, sink, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not a test failure.
        }
    }
}
