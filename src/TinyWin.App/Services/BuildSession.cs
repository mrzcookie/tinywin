using CommunityToolkit.Mvvm.ComponentModel;
using TinyWin.Catalog;
using TinyWin.Catalog.Models;
using TinyWin.Catalog.Resolution;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;

namespace TinyWin.App.Services;

/// <summary>
/// Everything the user has chosen so far, shared by every page.
/// </summary>
/// <remarks>
/// The pages are steps in one workflow rather than independent screens, so the selection lives here
/// rather than in any single view model. This is the only mutable state in the UI layer; view models
/// project it, they do not duplicate it.
///
/// Properties are declared <c>partial</c> because the MVVM toolkit's field-based form generates
/// code CsWinRT cannot marshal (MVVMTK0045).
/// </remarks>
public sealed partial class BuildSession : ObservableObject
{
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    public BuildSession()
    {
        Catalog = new CatalogDocument();
        Tweaks = new UnattendOptions();
        OutputIsoPath = string.Empty;
    }

    [ObservableProperty]
    public partial CatalogDocument Catalog { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial WindowsImageInfo? Image { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEdition))]
    [NotifyPropertyChangedFor(nameof(TargetBuild))]
    [NotifyPropertyChangedFor(nameof(MediaSupport))]
    public partial ImageEdition? Edition { get; set; }

    /// <summary>Id of the preset the selection came from, or null once the user has hand-edited it.</summary>
    [ObservableProperty]
    public partial string? PresetId { get; set; }

    [ObservableProperty]
    public partial UnattendOptions Tweaks { get; set; }

    [ObservableProperty]
    public partial string OutputIsoPath { get; set; }

    /// <summary>Set by the Build page when a run finishes, consumed by the Done page.</summary>
    [ObservableProperty]
    public partial BuildReport? Report { get; set; }

    public bool HasImage => Image is not null;

    public bool HasEdition => Edition is not null;

    /// <summary>Raised whenever the component selection changes, from any page.</summary>
    public event EventHandler? SelectionChanged;

    public IReadOnlyCollection<string> SelectedComponentIds => _selected;

    /// <summary>
    /// Build number the catalog is matched against. Falls back to the minimum supported build so the
    /// Customize page is browsable before an ISO has been chosen.
    /// </summary>
    public int TargetBuild => Edition?.Build ?? MediaSupportPolicy.MinimumSupportedBuild;

    public MediaSupport MediaSupport => MediaSupportPolicy.Classify(TargetBuild);

    public bool IsSelected(string componentId) => _selected.Contains(componentId);

    public void SetSelected(string componentId, bool selected)
    {
        var changed = selected ? _selected.Add(componentId) : _selected.Remove(componentId);
        if (changed)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ReplaceSelection(IEnumerable<string> componentIds)
    {
        ArgumentNullException.ThrowIfNull(componentIds);

        _selected.Clear();
        foreach (var id in componentIds)
        {
            _selected.Add(id);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resolves the current selection, or reports why it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// A conflict is a normal intermediate state while the user is clicking around, so this returns
    /// the problems instead of throwing — the header shows them and the Build gate stays shut.
    /// </remarks>
    public bool TryResolvePlan(out ResolvedPlan? plan, out IReadOnlyList<string> problems)
    {
        try
        {
            plan = PlanResolver.Resolve(Catalog, _selected, TargetBuild);
            problems = [];
            return true;
        }
        catch (PlanResolutionException ex)
        {
            plan = null;
            problems = ex.Problems;
            return false;
        }
    }

    /// <summary>The components actually selected, in catalog order.</summary>
    public IReadOnlyList<Component> SelectedComponents() =>
        [.. Catalog.Components.Where(c => _selected.Contains(c.Id))];

    public RiskTier HighestRisk()
    {
        var selected = SelectedComponents();
        return selected.Count == 0 ? RiskTier.Safe : selected.Max(c => c.Risk);
    }
}
