using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TinyWin.App.Demo;
using TinyWin.App.Services;
using TinyWin.Core.Pipeline;

namespace TinyWin.App.ViewModels;

/// <summary>One row of the stage list.</summary>
public sealed partial class StageRow : ObservableObject
{
    public StageRow(BuildStageId id, string title)
    {
        Id = id;
        Title = title;
        Detail = string.Empty;
    }

    public BuildStageId Id { get; }

    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    public partial StageState State { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; }

    public bool IsRunning => State == StageState.Running;

    public string Glyph => State switch
    {
        StageState.Completed => "",  // checkmark
        StageState.Failed => "",     // error badge
        StageState.Skipped => "",    // cancel / dash
        _ => "",                     // circle ring
    };
}

/// <summary>
/// The Build page: runs the pipeline, shows stage state, progress, elapsed time and the live log.
/// </summary>
/// <remarks>
/// Runs the real <see cref="BuildPipeline"/> over demo stages. That is deliberate — stage ordering,
/// skip reporting, progress plumbing, cancellation and the rollback unwind are all exercised for
/// real, so swapping in the M1 stage list changes one line in <see cref="CreateStages"/> and
/// nothing else.
/// </remarks>
public sealed partial class BuildViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _elapsed = new();

    private CancellationTokenSource? _cancellation;
    private int _runnableStageCount;
    private int _completedStageCount;
    private double _currentStagePercent;

    public BuildViewModel(BuildSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
        StatusText = "Ready to build.";
        ElapsedText = "0:00";
        EtaText = "estimating…";
        _timer.Tick += (_, _) => RefreshTiming();

        foreach (var id in Enum.GetValues<BuildStageId>())
        {
            Stages.Add(new StageRow(id, ActionDescriber.StageTitle(id)));
        }
    }

    public BuildSession Session { get; }

    public ObservableCollection<StageRow> Stages { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    [ObservableProperty]
    public partial bool Succeeded { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial double OverallPercent { get; set; }

    [ObservableProperty]
    public partial string ElapsedText { get; set; }

    [ObservableProperty]
    public partial string EtaText { get; set; }

    /// <summary>Set by the shell before navigation, from the Review page's gate.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool IsUnlocked { get; set; }

    public bool CanStart => IsUnlocked && !IsRunning;

    public bool CanCancel => IsRunning;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning || !IsUnlocked)
        {
            return;
        }

        if (!Session.TryResolvePlan(out var plan, out var problems))
        {
            StatusText = "Cannot start: " + string.Join("; ", problems);
            return;
        }

        Reset();
        IsRunning = true;
        IsFinished = false;
        StatusText = "Building…";
        _elapsed.Restart();
        _timer.Start();

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();

        var request = new BuildRequest
        {
            SourceIsoPath = Session.Image?.SourceIsoPath ?? string.Empty,
            OutputIsoPath = Session.OutputIsoPath,
            EditionIndex = Session.Edition?.Index ?? 1,
            ComponentIds = [.. Session.SelectedComponentIds],
            Unattend = Session.Tweaks,
            ScratchDirectory = Path.Combine(Path.GetTempPath(), "TinyWin"),
        };

        var context = new BuildContext
        {
            Request = request,
            Plan = plan!,
            ImageInfo = Session.Image,
            InstallWimPath = "sources\\install.wim",
        };

        var stages = CreateStages();
        _runnableStageCount = stages.Count(s => s.ShouldRun(context));

        Append($"TinyWin {typeof(BuildViewModel).Assembly.GetName().Version}");
        Append($"Source   {request.SourceIsoPath}");
        Append($"Output   {request.OutputIsoPath}");
        Append($"Edition  index {request.EditionIndex}");
        Append($"Plan     {plan!.ComponentIds.Count} components, {plan.ImageActions.Count + plan.RegistryActions.Count} actions");
        Append(string.Empty);

        var progress = new Progress<BuildProgress>(OnProgress);
        var pipeline = new BuildPipeline(stages);

        var report = await pipeline.RunAsync(context, progress, _cancellation.Token);
        var cancelled = _cancellation.IsCancellationRequested;

        _timer.Stop();
        _elapsed.Stop();
        Finish(report, cancelled);
    }

    public void Dispose() => _cancellation?.Dispose();

    [RelayCommand]
    private void Cancel()
    {
        if (_cancellation is { IsCancellationRequested: false })
        {
            StatusText = "Cancelling — unwinding safely…";
            Append("Cancel requested. The pipeline will dismount with /discard before stopping.");
            _cancellation.Cancel();
        }
    }

    [RelayCommand]
    private void CopyLog()
    {
        var text = new StringBuilder();
        foreach (var line in Log)
        {
            text.AppendLine(line);
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        StatusText = $"Copied {Log.Count} log lines to the clipboard.";
    }

    private IReadOnlyList<IBuildStage> CreateStages() =>
        DemoPipelineFactory.Create(line => _dispatcher.TryEnqueue(() => Append(line)));

    private void OnProgress(BuildProgress progress)
    {
        var row = Stages.FirstOrDefault(s => s.Id == progress.Stage);
        if (row is null)
        {
            return;
        }

        // The pipeline announces a stage starting but not finishing, so anything still marked
        // running once a later stage speaks up has in fact completed.
        if (progress.State is StageState.Running or StageState.Skipped)
        {
            foreach (var earlier in Stages.Where(s => s.Id < progress.Stage && s.State == StageState.Running))
            {
                earlier.State = StageState.Completed;
                earlier.Detail = "done";
                _completedStageCount++;
            }
        }

        row.State = progress.State;
        row.Detail = progress.Message;
        _currentStagePercent = progress.StagePercent ?? 0;

        if (progress.State == StageState.Skipped)
        {
            Append($"skipped     {row.Title} — {progress.Message}");
        }
        else if (progress.State == StageState.Failed)
        {
            Append($"FAILED      {progress.Message}");
        }
        else if (progress.StagePercent is null or >= 1.0)
        {
            Append($"stage       {row.Title}");
        }

        RefreshTiming();
    }

    private void Finish(BuildReport report, bool cancelled)
    {
        // The report is authoritative for final stage state — progress events cannot see a stage
        // that failed during rollback.
        foreach (var stageReport in report.Stages)
        {
            var row = Stages.FirstOrDefault(s => s.Id == stageReport.Stage);
            if (row is null)
            {
                continue;
            }

            row.State = stageReport.State;
            row.Detail = stageReport.Error ?? (stageReport.Duration > TimeSpan.Zero
                ? Formatting.Duration(stageReport.Duration)
                : row.Detail);
        }

        foreach (var pending in Stages.Where(s => s.State == StageState.Running))
        {
            pending.State = StageState.Pending;
        }

        IsRunning = false;
        IsFinished = true;
        Succeeded = report.Succeeded;
        OverallPercent = report.Succeeded ? 100 : OverallPercent;

        StatusText = report.Succeeded
            ? $"Build complete in {Formatting.Duration(report.TotalDuration)}."
            : cancelled
                ? "Build cancelled. The image was dismounted with /discard — nothing was left mounted."
                : "Build failed. See the log.";

        Append(string.Empty);
        Append(StatusText);
        Append($"applied {report.AppliedCount}   no target {report.NoTargetCount}   failed {report.FailedCount}");

        foreach (var warning in report.Warnings)
        {
            Append($"warning     {warning}");
        }

        // A real Verify stage measures the file it just wrote. Until then the Done page shows the
        // same estimate the Customize header did, compression factor and all.
        var sourceSize = Session.Image?.TotalSizeBytes ?? 0;
        var payloadMb = Session.Catalog.Components
            .Where(c => Session.IsSelected(c.Id))
            .Sum(c => c.EstimatedSavingsMb);

        Session.Report = report with
        {
            SourceSizeBytes = sourceSize,
            OutputSizeBytes = SizeEstimator.EstimatedOutputBytes(sourceSize, payloadMb),
        };

        EtaText = "—";
        RefreshTiming();
    }

    private void Reset()
    {
        Log.Clear();
        _completedStageCount = 0;
        _currentStagePercent = 0;
        OverallPercent = 0;
        Succeeded = false;

        foreach (var stage in Stages)
        {
            stage.State = StageState.Pending;
            stage.Detail = string.Empty;
        }
    }

    private void RefreshTiming()
    {
        ElapsedText = Formatting.Duration(_elapsed.Elapsed);

        if (_runnableStageCount > 0)
        {
            var fraction = Math.Clamp(
                (_completedStageCount + _currentStagePercent) / _runnableStageCount, 0, 1);

            OverallPercent = fraction * 100;

            if (IsRunning && fraction > 0.02)
            {
                var remaining = TimeSpan.FromSeconds(_elapsed.Elapsed.TotalSeconds * (1 - fraction) / fraction);
                EtaText = $"about {Formatting.Duration(remaining)} left";
            }
        }
    }

    private void Append(string line)
    {
        Log.Add(line.Length == 0
            ? string.Empty
            : $"[{_elapsed.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture)}]  {line}");

        // A real run logs tens of thousands of lines; the pane keeps the tail.
        while (Log.Count > 5000)
        {
            Log.RemoveAt(0);
        }
    }
}
