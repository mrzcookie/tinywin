using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyWin.App.Services;
using TinyWin.Catalog;
using TinyWin.Catalog.Models;
using TinyWin.Core.Models;

namespace TinyWin.App.ViewModels;

/// <summary>A component's per-action tally on the Done page.</summary>
public sealed record ComponentTally(string Name, int Applied, int NoTarget, int Failed)
{
    public string CountsText => $"{Applied} applied  ·  {NoTarget} no target  ·  {Failed} failed";

    public bool HasNoTarget => NoTarget > 0;
}

/// <summary>
/// The Done page: what actually happened.
/// </summary>
public sealed partial class DoneViewModel : ObservableObject
{
    public DoneViewModel(BuildSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
        StatusMessage = string.Empty;
    }

    public BuildSession Session { get; }

    public ObservableCollection<ComponentTally> Tallies { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    public bool HasReport => Session.Report is not null;

    public bool Succeeded => Session.Report?.Succeeded ?? false;

    public string HeadlineText => Session.Report is null
        ? "No build has run yet."
        : Session.Report.Succeeded
            ? "Your ISO is ready."
            : "The build did not finish.";

    public string SizeBeforeText => Formatting.Bytes(Session.Report?.SourceSizeBytes ?? 0);

    public string SizeAfterText => Formatting.Bytes(Session.Report?.OutputSizeBytes ?? 0);

    public string SizeDeltaText
    {
        get
        {
            if (Session.Report is not { } report || report.SourceSizeBytes == 0)
            {
                return "—";
            }

            var saved = report.SourceSizeBytes - report.OutputSizeBytes;
            var percent = (double)saved / report.SourceSizeBytes;
            return $"−{Formatting.Bytes(saved)}  ({percent.ToString("P0", CultureInfo.CurrentCulture)} smaller)";
        }
    }

    public string DurationText =>
        Session.Report is { } report ? Formatting.Duration(report.TotalDuration) : "—";

    public string OutputPathText => Session.Report?.OutputIsoPath ?? Session.OutputIsoPath;

    public int AppliedCount => Session.Report?.AppliedCount ?? 0;

    public int NoTargetCount => Session.Report?.NoTargetCount ?? 0;

    public int FailedCount => Session.Report?.FailedCount ?? 0;

    public bool HasNoTargets => NoTargetCount > 0;

    /// <summary>
    /// The no-op explanation. Shown whenever the count is above zero, never hidden behind an
    /// expander — a build that quietly found nothing to remove is the failure this whole design is
    /// meant to catch (docs/PLAN.md section 2.1).
    /// </summary>
    public string NoTargetExplanation =>
        $"{NoTargetCount} of {AppliedCount + NoTargetCount + FailedCount} actions found no target on this media. " +
        "That is expected for actions marked optional, and a sign of catalog drift otherwise. Every one is listed in the log.";

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasOutput => Succeeded && !string.IsNullOrEmpty(OutputPathText);

    public void Refresh()
    {
        Tallies.Clear();
        Warnings.Clear();

        if (Session.Report is { } report)
        {
            foreach (var group in report.Actions.GroupBy(a => a.ComponentId, StringComparer.OrdinalIgnoreCase))
            {
                Tallies.Add(new ComponentTally(
                    Session.Catalog.Find(group.Key)?.Name ?? group.Key,
                    group.Count(a => a.Status == ActionStatus.Applied),
                    group.Count(a => a.Status == ActionStatus.NoTarget),
                    group.Count(a => a.Status == ActionStatus.Failed)));
            }

            foreach (var warning in report.Warnings)
            {
                Warnings.Add(warning);
            }
        }

        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void RevealInExplorer()
    {
        var path = OutputPathText;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // The output does not exist yet — the pipeline is a demo — so fall back to the folder.
        var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
            StatusMessage = string.Empty;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            StatusMessage = $"Could not open Explorer: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsPresetAsync()
    {
        var path = await FilePickers.PickSaveAsync("tinywin-preset", ".json", "TinyWin preset");
        if (path is null)
        {
            return;
        }

        var document = new CatalogDocument
        {
            SchemaVersion = 1,
            Presets =
            [
                new Preset
                {
                    Id = "custom",
                    Name = "Custom",
                    Description = $"Saved from a TinyWin build on {DateTimeOffset.Now:yyyy-MM-dd}.",
                    Order = 100,
                    MaxRisk = Session.HighestRisk(),
                    Includes = [.. Session.SelectedComponentIds.OrderBy(i => i, StringComparer.Ordinal)],
                },
            ],
        };

        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, CatalogJson.Options));
            StatusMessage = $"Saved these choices to {Path.GetFileName(path)}.";
        }
        catch (IOException ex)
        {
            StatusMessage = $"Could not save the preset: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = $"Could not save the preset: {ex.Message}";
        }
    }
}
