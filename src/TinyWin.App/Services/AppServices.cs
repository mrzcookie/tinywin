using Microsoft.UI.Xaml;
using TinyWin.App.Demo;
using TinyWin.App.ViewModels;
using TinyWin.Catalog;
using TinyWin.Core.Abstractions;

namespace TinyWin.App.Services;

/// <summary>
/// The composition root.
/// </summary>
/// <remarks>
/// Deliberately a handful of static members rather than a container. The app has exactly one of
/// each of these, the graph is three objects deep, and a portable single-file exe has no use for
/// assembly scanning. Swapping <see cref="Imaging"/> for the real backend is a one-line change here.
///
/// <see cref="Initialize"/> is explicit rather than a static initializer because the view models
/// capture the UI thread's <c>DispatcherQueue</c>, so construction order and thread both matter.
/// </remarks>
public static class AppServices
{
    private static readonly EmbeddedCatalogProvider CatalogProvider = new();
    private static ShellViewModel? _shell;

    public static BuildSession Session { get; } = new();

    /// <summary>
    /// The only route the UI has to an image. Currently a demo stand-in — see
    /// <c>DemoImagingBackend</c>. The UI must never reach past this to DISM.
    /// </summary>
    public static IImagingBackend Imaging { get; } = new DemoImagingBackend();

    /// <summary>Set once at launch. Needed to parent file pickers and dialogs.</summary>
    public static Window? MainWindow { get; set; }

    /// <summary>The page view models, created once and kept for the life of the window.</summary>
    public static ShellViewModel Shell =>
        _shell ?? throw new InvalidOperationException($"{nameof(Initialize)} has not been called.");

    /// <summary>Builds the view model graph. Must run on the UI thread.</summary>
    public static void Initialize() => _shell ??= new ShellViewModel(Session, Imaging);

    public static async Task LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        Session.Catalog = await CatalogProvider.LoadAsync(cancellationToken).ConfigureAwait(true);
        Shell.Customize.Build();
    }
}
