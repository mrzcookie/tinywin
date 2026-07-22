using CommunityToolkit.Mvvm.ComponentModel;
using TinyWin.App.Services;
using TinyWin.Catalog.Models;

namespace TinyWin.App.ViewModels;

/// <summary>One component row in the Customize tree.</summary>
/// <remarks>
/// Holds no selection state of its own — <see cref="IsSelected"/> reads and writes the session, so
/// a preset applied from the header and a checkbox clicked in the tree cannot disagree.
/// </remarks>
public sealed partial class ComponentNode : ObservableObject
{
    private readonly CustomizeViewModel _owner;

    public ComponentNode(CustomizeViewModel owner, CategoryNode parent, Component model)
    {
        _owner = owner;
        Parent = parent;
        Model = model;
    }

    public Component Model { get; }

    public CategoryNode Parent { get; }

    public string Id => Model.Id;

    public string Name => Model.Name;

    public string Description => Model.Description;

    public RiskTier Risk => Model.Risk;

    public string RiskLabel => Model.Risk switch
    {
        RiskTier.Safe => "Safe",
        RiskTier.Caution => "Caution",
        RiskTier.Breaking => "Breaking",
        RiskTier.Unserviceable => "Unserviceable",
        _ => Model.Risk.ToString(),
    };

    public string SavingsText => Formatting.Megabytes(Model.EstimatedSavingsMb);

    public IReadOnlyList<string> Breaks => Model.Breaks;

    public bool HasBreaks => Model.Breaks.Count > 0;

    public int ActionCount => Model.Actions.Count;

    public string ActionCountText => Model.Actions.Count == 1 ? "1 action" : $"{Model.Actions.Count} actions";

    /// <summary>Non-empty when this component drags others in, which the flyout has to say out loud.</summary>
    public string RequiresText => Model.Requires.Count == 0
        ? string.Empty
        : "Also selects: " + string.Join(", ", Model.Requires);

    public bool HasRequires => Model.Requires.Count > 0;

    /// <summary>True when this component is unvalidated against the media currently loaded.</summary>
    public bool IsOutOfRange => !Model.AppliesTo.Includes(_owner.Session.TargetBuild);

    public string AppliesToText =>
        $"Validated for builds {Model.AppliesTo.MinBuild?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "any"}" +
        $"–{Model.AppliesTo.MaxBuild?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "any"}";

    public bool IsSelected
    {
        get => _owner.Session.IsSelected(Model.Id);
        set
        {
            if (value == IsSelected)
            {
                return;
            }

            _owner.SetComponentSelected(this, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads selection from the session after something else changed it.</summary>
    public void RefreshSelection()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsOutOfRange));
    }
}

/// <summary>A category row in the Customize tree, with a tri-state checkbox over its children.</summary>
public sealed partial class CategoryNode : ObservableObject
{
    public CategoryNode(string name) => Name = name;

    public string Name { get; }

    /// <summary>Every component in this category, regardless of the current search filter.</summary>
    public List<ComponentNode> All { get; } = [];

    /// <summary>The components currently visible. The tri-state checkbox operates on these only,
    /// so "select all" during a search means "select all matches", not "select all hidden things".</summary>
    public System.Collections.ObjectModel.ObservableCollection<ComponentNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public int SelectedCount => Children.Count(c => c.IsSelected);

    public string SummaryText => $"{SelectedCount} of {Children.Count} selected";

    public string SavingsText => Formatting.Megabytes(Children.Where(c => c.IsSelected).Sum(c => c.Model.EstimatedSavingsMb));

    /// <summary>
    /// Tri-state over the visible children.
    /// </summary>
    /// <remarks>
    /// A three-state <c>CheckBox</c> cycles unchecked → checked → indeterminate on click. Mapping
    /// the indeterminate write to "clear everything" turns that cycle into the two-step users
    /// expect, and it errs towards deselecting — which is the safe direction for a tool whose
    /// checkboxes delete things.
    /// </remarks>
    public bool? IsChecked
    {
        get
        {
            if (Children.Count == 0)
            {
                return false;
            }

            var selected = SelectedCount;
            return selected == 0 ? false : selected == Children.Count ? true : null;
        }

        set => SetAll(value == true);
    }

    public void SetAll(bool selected)
    {
        foreach (var child in Children)
        {
            child.IsSelected = selected;
        }

        RefreshChecked();
    }

    public void RefreshChecked()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SavingsText));
    }
}
