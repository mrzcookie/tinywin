using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TinyWin.App.Services;
using TinyWin.Catalog.Models;
using TinyWin.Core.Models;
using TinyWin.Core.Pipeline;

namespace TinyWin.App.ViewModels;

public sealed record ReviewItem(string Text, string ComponentName);

/// <summary>Actions grouped by the pipeline stage that will run them.</summary>
public sealed class ReviewGroup(string title, string subtitle, IReadOnlyList<ReviewItem> items)
{
    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle;

    public IReadOnlyList<ReviewItem> Items { get; } = items;

    public string CountText => Items.Count == 1 ? "1 action" : $"{Items.Count} actions";
}

/// <summary>
/// The Review page: everything that will happen, and the gate in front of it.
/// </summary>
public sealed partial class ReviewViewModel : ObservableObject
{
    /// <summary>
    /// What the user must type to unlock an unserviceable build.
    /// </summary>
    /// <remarks>
    /// A word rather than a checkbox because the unserviceable tier produces an image Microsoft
    /// will not service and which cannot be repaired in place. A click is muscle memory; typing is
    /// not. See docs/PLAN.md section 4.
    /// </remarks>
    public const string RequiredConfirmation = "UNSERVICEABLE";

    public ReviewViewModel(BuildSession session, TweaksViewModel tweaks)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tweaks);

        Session = session;
        _tweaks = tweaks;
        ConfirmationText = string.Empty;
    }

    private readonly TweaksViewModel _tweaks;

    public BuildSession Session { get; }

    public ObservableCollection<ReviewGroup> Groups { get; } = [];

    public ObservableCollection<string> ResolverWarnings { get; } = [];

    [ObservableProperty]
    public partial string ConfirmationText { get; set; }

    public RiskTier HighestRisk { get; private set; } = RiskTier.Safe;

    public int ActionCount { get; private set; }

    public int ComponentCount { get; private set; }

    public int EstimatedSavingsMb { get; private set; }

    public string SummaryText =>
        $"{ComponentCount} components  ·  {ActionCount} actions  ·  ~{Formatting.Megabytes(EstimatedSavingsMb)} saved";

    public string SourceText => Session.Image is { } image
        ? $"{Path.GetFileName(image.SourceIsoPath)}  ({Formatting.Bytes(image.TotalSizeBytes)})"
        : "No ISO selected";

    public string EditionText => Session.Edition is { } edition
        ? $"{edition.Name} — index {edition.Index}, {edition.Architecture}, build {edition.Version.Build}"
        : "No edition selected";

    public string OutputText => Session.OutputIsoPath.Length > 0 ? Session.OutputIsoPath : "Not set";

    public bool HasSource => Session.Image is not null && Session.Edition is not null;

    public bool HasWarnings => ResolverWarnings.Count > 0;

    // Escalating InfoBars, one tier at a time.
    public bool ShowCautionNotice => HighestRisk == RiskTier.Caution;

    public bool ShowBreakingNotice => HighestRisk == RiskTier.Breaking;

    public bool RequiresConfirmation => HighestRisk == RiskTier.Unserviceable;

    public bool IsConfirmed =>
        !RequiresConfirmation ||
        string.Equals(ConfirmationText?.Trim(), RequiredConfirmation, StringComparison.Ordinal);

    public string ConfirmationPrompt =>
        $"Type {RequiredConfirmation} to unlock the build. Once {UnserviceableComponentsText} is gone, the " +
        "resulting image cannot be updated, repaired, or have the feature added back.";

    /// <summary>Names of the unserviceable components, so the gate says what it is gating.</summary>
    public string UnserviceableComponentsText =>
        string.Join(", ", Session.SelectedComponents()
            .Where(c => c.Risk == RiskTier.Unserviceable)
            .Select(c => c.Name));

    /// <summary>Everything the Build page checks before it will start.</summary>
    public bool CanBuild =>
        HasSource
        && ComponentCount > 0
        && ResolvesCleanly
        && Session.MediaSupport != MediaSupport.Unsupported
        && IsConfirmed;

    public bool ResolvesCleanly { get; private set; }

    public string BlockedReason
    {
        get
        {
            if (!HasSource)
            {
                return "Choose an ISO and an edition on the Source page first.";
            }

            if (Session.MediaSupport == MediaSupport.Unsupported)
            {
                return "That media is older than 24H2 (26100) and is not supported.";
            }

            if (!ResolvesCleanly)
            {
                return "The current selection has a conflict. Fix it on the Customize page.";
            }

            if (ComponentCount == 0)
            {
                return "Nothing is selected. Pick a preset or some components on the Customize page.";
            }

            return RequiresConfirmation && !IsConfirmed
                ? $"Type {RequiredConfirmation} below to unlock the build."
                : string.Empty;
        }
    }

    /// <summary>Rebuilds the summary. Called every time the page is navigated to.</summary>
    public void Refresh()
    {
        Groups.Clear();
        ResolverWarnings.Clear();

        ResolvesCleanly = Session.TryResolvePlan(out var plan, out var problems);

        if (!ResolvesCleanly || plan is null)
        {
            foreach (var problem in problems)
            {
                ResolverWarnings.Add(problem);
            }

            ComponentCount = 0;
            ActionCount = 0;
            EstimatedSavingsMb = 0;
            HighestRisk = RiskTier.Safe;
            RaiseAll();
            return;
        }

        ComponentCount = plan.ComponentIds.Count;
        EstimatedSavingsMb = plan.EstimatedSavingsMb;
        HighestRisk = plan.HighestRisk;

        foreach (var warning in plan.Warnings)
        {
            ResolverWarnings.Add($"{Name(warning.ComponentId)}: {warning.Message}");
        }

        var imageItems = plan.ImageActions
            .SelectMany(a => ActionDescriber.Describe(a.Action).Select(d => new ReviewItem(d, Name(a.ComponentId))))
            .ToList();

        var registryItems = plan.RegistryActions
            .SelectMany(a => ActionDescriber.Describe(a.Action).Select(d => new ReviewItem(d, Name(a.ComponentId))))
            .ToList();

        var tweakItems = _tweaks.Descriptions()
            .Where(t => t.Enabled)
            .Select(t => new ReviewItem(t.Title, "autounattend.xml"))
            .ToList();

        if (imageItems.Count > 0)
        {
            Groups.Add(new ReviewGroup(
                ActionDescriber.StageTitle(BuildStageId.ApplyComponents),
                "Runs against the mounted image, in the order shown: appx, capabilities, features, packages, services, tasks, files.",
                imageItems));
        }

        if (registryItems.Count > 0)
        {
            Groups.Add(new ReviewGroup(
                ActionDescriber.StageTitle(BuildStageId.ApplyRegistry),
                "Applied inside a single offline hive session, then the hives are unloaded.",
                registryItems));
        }

        if (tweakItems.Count > 0)
        {
            Groups.Add(new ReviewGroup(
                ActionDescriber.StageTitle(BuildStageId.WriteUnattend),
                "Setup behaviour, written into the ISO root. Reversible after install — nothing here is a removal.",
                tweakItems));
        }

        ActionCount = imageItems.Count + registryItems.Count;
        RaiseAll();
    }

    partial void OnConfirmationTextChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsConfirmed));
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(BlockedReason));
    }

    private string Name(string componentId) => Session.Catalog.Find(componentId)?.Name ?? componentId;

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(EditionText));
        OnPropertyChanged(nameof(OutputText));
        OnPropertyChanged(nameof(HighestRisk));
        OnPropertyChanged(nameof(ShowCautionNotice));
        OnPropertyChanged(nameof(ShowBreakingNotice));
        OnPropertyChanged(nameof(RequiresConfirmation));
        OnPropertyChanged(nameof(UnserviceableComponentsText));
        OnPropertyChanged(nameof(ConfirmationPrompt));
        OnPropertyChanged(nameof(IsConfirmed));
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(BlockedReason));
        OnPropertyChanged(nameof(ResolvesCleanly));
    }
}
