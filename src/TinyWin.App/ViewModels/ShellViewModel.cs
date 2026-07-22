using CommunityToolkit.Mvvm.ComponentModel;
using TinyWin.App.Services;
using TinyWin.Core.Abstractions;

namespace TinyWin.App.ViewModels;

/// <summary>
/// Owns the per-page view models and the gate in front of the Build page.
/// </summary>
/// <remarks>
/// The view models outlive their pages so a round trip through the navigation frame does not lose
/// the log, the search text, or a half-typed confirmation.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(BuildSession session, IImagingBackend imaging)
    {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
        Source = new SourceViewModel(session, imaging);
        Customize = new CustomizeViewModel(session);
        Tweaks = new TweaksViewModel(session);
        Review = new ReviewViewModel(session, Tweaks);
        Build = new BuildViewModel(session);
        Done = new DoneViewModel(session);
    }

    public BuildSession Session { get; }

    public SourceViewModel Source { get; }

    public CustomizeViewModel Customize { get; }

    public TweaksViewModel Tweaks { get; }

    public ReviewViewModel Review { get; }

    public BuildViewModel Build { get; }

    public DoneViewModel Done { get; }

    /// <summary>
    /// True only once the Review page's gate is satisfied.
    /// </summary>
    /// <remarks>
    /// The single reason the Build page cannot be reached from the launch screen: the destructive
    /// step is behind a page that first shows every action and, for the unserviceable tier, demands
    /// a typed confirmation.
    /// </remarks>
    public bool CanBuild => Review.CanBuild;

    public string BuildBlockedReason => Review.BlockedReason;

    /// <summary>Re-evaluates the gate. Called on every navigation.</summary>
    public void RefreshGate()
    {
        Review.Refresh();
        Build.IsUnlocked = Review.CanBuild;
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(BuildBlockedReason));
    }
}
