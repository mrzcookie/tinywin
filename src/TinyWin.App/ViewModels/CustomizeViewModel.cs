using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyWin.App.Services;
using TinyWin.Catalog;
using TinyWin.Catalog.Models;
using TinyWin.Catalog.Resolution;

namespace TinyWin.App.ViewModels;

/// <summary>A row in the preset dropdown. "Custom" is a real entry rather than a null.</summary>
public sealed record PresetChoice(string? Id, string Name, string Description)
{
    public static PresetChoice Custom { get; } =
        new(null, "Custom", "Your own selection. Export it to reuse it on another machine.");
}

/// <summary>
/// The Customize page: the searchable component tree, the preset dropdown, and the live estimate.
/// </summary>
public sealed partial class CustomizeViewModel : ObservableObject
{
    private readonly List<CategoryNode> _allCategories = [];
    private bool _suppressPresetReset;

    public CustomizeViewModel(BuildSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
        SearchText = string.Empty;
        StatusMessage = string.Empty;
        Session.SelectionChanged += OnSessionSelectionChanged;
        Build();
    }

    public BuildSession Session { get; }

    public ObservableCollection<CategoryNode> Categories { get; } = [];

    public ObservableCollection<PresetChoice> Presets { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial PresetChoice? SelectedPreset { get; set; }

    /// <summary>Conflict text from the resolver, or an import/export result. Empty means all well.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsStatusError { get; set; }

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public int SelectedCount => Session.SelectedComponentIds.Count;

    public string SelectedCountText =>
        SelectedCount == 1 ? "1 component selected" : $"{SelectedCount} components selected";

    /// <summary>
    /// The saving as it will show up on the ISO, not the uncompressed payload the catalog records.
    /// </summary>
    public string SavingsText => Formatting.Bytes(SizeEstimator.IsoSavingsBytes(EstimatedSavingsMb));

    public string SizeBeforeText =>
        Session.Image is { } image ? Formatting.Bytes(image.TotalSizeBytes) : "—";

    public string SizeAfterText =>
        Session.Image is { } image
            ? Formatting.Bytes(SizeEstimator.EstimatedOutputBytes(image.TotalSizeBytes, EstimatedSavingsMb))
            : "—";

    /// <summary>Uncompressed payload removed — the figure the catalog actually states.</summary>
    public string PayloadText => Formatting.Megabytes(EstimatedSavingsMb);

    public bool HasSizeEstimate => Session.Image is not null;

    /// <summary>
    /// Savings for the resolved selection, not the raw checkbox list — a component pulled in by a
    /// <c>requires</c> edge counts too, and the header must not understate what is about to happen.
    /// </summary>
    public int EstimatedSavingsMb =>
        Session.TryResolvePlan(out var plan, out _) ? plan!.EstimatedSavingsMb : 0;

    public RiskTier HighestRisk => Session.HighestRisk();

    public string HighestRiskLabel => HighestRisk switch
    {
        RiskTier.Safe => "Safe",
        RiskTier.Caution => "Caution",
        RiskTier.Breaking => "Breaking",
        RiskTier.Unserviceable => "Unserviceable",
        _ => HighestRisk.ToString(),
    };

    /// <summary>Rebuilds the tree from the catalog. Called once the catalog has finished loading.</summary>
    public void Build()
    {
        _allCategories.Clear();
        Presets.Clear();

        foreach (var group in Session.Catalog.Components
                     .GroupBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => CategoryOrder(g.Key))
                     .ThenBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var category = new CategoryNode(group.Key) { IsExpanded = true };
            category.All.AddRange(group
                .OrderBy(c => c.Risk)
                .ThenBy(c => c.Name, StringComparer.CurrentCulture)
                .Select(c => new ComponentNode(this, category, c)));

            _allCategories.Add(category);
        }

        foreach (var preset in Session.Catalog.Presets.OrderBy(p => p.Order))
        {
            Presets.Add(new PresetChoice(preset.Id, preset.Name, preset.Description));
        }

        Presets.Add(PresetChoice.Custom);

        ApplyFilter();
        SyncPresetSelection();
        RefreshHeader();
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        ApplyFilter();
    }

    partial void OnSelectedPresetChanged(PresetChoice? value)
    {
        if (_suppressPresetReset || value is null || value.Id is null)
        {
            return;
        }

        ApplyPreset(value.Id);
    }

    partial void OnStatusMessageChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasStatus));
    }

    /// <summary>
    /// Applies a shipped preset, replacing the current selection outright.
    /// </summary>
    public void ApplyPreset(string presetId)
    {
        var ids = PlanResolver.ExpandPreset(Session.Catalog, presetId);
        _suppressPresetReset = true;
        Session.ReplaceSelection(ids);
        Session.PresetId = presetId;
        _suppressPresetReset = false;

        RefreshAllNodes();
        RefreshHeader();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        Session.ReplaceSelection([]);
        Session.PresetId = null;
        SyncPresetSelection();
        RefreshAllNodes();
        RefreshHeader();
    }

    /// <summary>
    /// Writes the current selection out as a one-preset catalog file, so it can be dropped into a
    /// catalog directory or re-imported here.
    /// </summary>
    [RelayCommand]
    private async Task ExportPresetAsync()
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
                    Description = $"Exported from TinyWin: {SelectedCount} components, {SavingsText} estimated saving.",
                    Order = 100,
                    MaxRisk = HighestRisk,
                    Includes = [.. Session.SelectedComponentIds.OrderBy(i => i, StringComparer.Ordinal)],
                },
            ],
        };

        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, CatalogJson.Options));
            SetStatus($"Saved {SelectedCount} components to {Path.GetFileName(path)}.", isError: false);
        }
        catch (IOException ex)
        {
            SetStatus($"Could not write the preset: {ex.Message}", isError: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            SetStatus($"Could not write the preset: {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task ImportPresetAsync()
    {
        var path = await FilePickers.PickOpenAsync(".json");
        if (path is null)
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var document = JsonSerializer.Deserialize<CatalogDocument>(json, CatalogJson.Options);

            if (document?.Presets is not { Count: > 0 } presets)
            {
                SetStatus("That file contains no presets.", isError: true);
                return;
            }

            var preset = presets[0];
            var known = preset.Includes.Where(id => Session.Catalog.Find(id) is not null).ToList();
            var unknown = preset.Includes.Count - known.Count;

            Session.ReplaceSelection(known);
            Session.PresetId = null;
            SyncPresetSelection();
            RefreshAllNodes();
            RefreshHeader();

            SetStatus(
                unknown == 0
                    ? $"Imported '{preset.Name}' — {known.Count} components."
                    : $"Imported '{preset.Name}' — {known.Count} components. {unknown} id(s) are not in this catalog and were skipped.",
                isError: unknown > 0);
        }
        catch (JsonException ex)
        {
            SetStatus($"That file is not a valid preset: {ex.Message}", isError: true);
        }
        catch (IOException ex)
        {
            SetStatus($"Could not read the preset: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Applies a checkbox change, then repairs the selection so it stays resolvable.
    /// </summary>
    /// <remarks>
    /// Both directions of a <c>requires</c> edge are handled here rather than left to the resolver.
    /// The resolver would silently pull a dependency in at build time; doing it at click time means
    /// the tree on screen is the truth, which matters when the thing being pulled in is Edge.
    /// </remarks>
    internal void SetComponentSelected(ComponentNode node, bool selected)
    {
        Session.SetSelected(node.Id, selected);

        if (selected)
        {
            foreach (var required in node.Model.Requires)
            {
                Session.SetSelected(required, true);
            }
        }
        else
        {
            foreach (var dependent in Session.Catalog.Components.Where(c =>
                         c.Requires.Contains(node.Id, StringComparer.OrdinalIgnoreCase)))
            {
                Session.SetSelected(dependent.Id, false);
            }
        }

        Session.PresetId = null;
        SyncPresetSelection();
        RefreshAllNodes(except: node);
        node.Parent.RefreshChecked();
        RefreshHeader();
    }

    private void OnSessionSelectionChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(SelectedCount));

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        var searching = query.Length > 0;

        Categories.Clear();

        foreach (var category in _allCategories)
        {
            category.Children.Clear();

            foreach (var component in category.All.Where(c => Matches(c, query)))
            {
                category.Children.Add(component);
            }

            if (category.Children.Count > 0)
            {
                if (searching)
                {
                    category.IsExpanded = true;
                }

                Categories.Add(category);
                category.RefreshChecked();
            }
        }
    }

    private static bool Matches(ComponentNode node, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        return node.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || node.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || node.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || node.Parent.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || node.Breaks.Any(b => b.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void RefreshAllNodes(ComponentNode? except = null)
    {
        foreach (var category in _allCategories)
        {
            foreach (var child in category.All.Where(c => !ReferenceEquals(c, except)))
            {
                child.RefreshSelection();
            }

            category.RefreshChecked();
        }
    }

    private void SyncPresetSelection()
    {
        _suppressPresetReset = true;
        SelectedPreset = Presets.FirstOrDefault(p =>
            string.Equals(p.Id, Session.PresetId, StringComparison.OrdinalIgnoreCase)) ?? PresetChoice.Custom;
        _suppressPresetReset = false;
    }

    private void RefreshHeader()
    {
        if (!Session.TryResolvePlan(out _, out var problems))
        {
            SetStatus(string.Join("  ", problems), isError: true);
        }
        else if (IsStatusError)
        {
            SetStatus(string.Empty, isError: false);
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(EstimatedSavingsMb));
        OnPropertyChanged(nameof(SavingsText));
        OnPropertyChanged(nameof(PayloadText));
        OnPropertyChanged(nameof(SizeBeforeText));
        OnPropertyChanged(nameof(SizeAfterText));
        OnPropertyChanged(nameof(HasSizeEstimate));
        OnPropertyChanged(nameof(HighestRisk));
        OnPropertyChanged(nameof(HighestRiskLabel));
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }

    /// <summary>Least dangerous categories first, so the destructive ones are never the default view.</summary>
    private static int CategoryOrder(string category) => category switch
    {
        "AI & Assistant" => 0,
        "Microsoft Apps" => 1,
        "Media & Casual" => 2,
        "Gaming" => 3,
        "Bing & Content" => 4,
        "Telemetry & Privacy" => 5,
        "Optional Features" => 6,
        "Language & Input" => 7,
        "System" => 8,
        "Unserviceable" => 9,
        _ => 5,
    };
}
