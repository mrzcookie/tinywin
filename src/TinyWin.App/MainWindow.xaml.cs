using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using TinyWin.App.Services;
using TinyWin.App.ViewModels;
using TinyWin.App.Views;

namespace TinyWin.App;

/// <summary>
/// The NavigationView shell. Owns page routing and the Build gate.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly (string Tag, Type Page)[] Routes =
    [
        ("home", typeof(HomePage)),
        ("source", typeof(SourcePage)),
        ("customize", typeof(CustomizePage)),
        ("tweaks", typeof(TweaksPage)),
        ("review", typeof(ReviewPage)),
        ("build", typeof(BuildPage)),
        ("done", typeof(DonePage)),
    ];

    public MainWindow()
    {
        InitializeComponent();

        Instance = this;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ViewModel = AppServices.Shell;
        ViewModel.Build.PropertyChanged += OnBuildStateChanged;

        ShowElevationState();
        Activated += OnFirstActivated;
    }

    public static MainWindow? Instance { get; private set; }

    public ShellViewModel ViewModel { get; }

    /// <summary>Lets a page move the shell — "Get started", "Go to Build", and so on.</summary>
    public void NavigateTo(string tag)
    {
        // Refresh first: a page asking to move to Build may itself be what satisfied the gate.
        RefreshGate();

        var items = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>();
        if (items.FirstOrDefault(i => (string?)i.Tag == tag) is { IsEnabled: true } item)
        {
            Nav.SelectedItem = item;
        }
    }

    /// <summary>Re-evaluates the Build gate and reflects it in the pinned nav items.</summary>
    public void RefreshGate()
    {
        ViewModel.RefreshGate();
        BuildNavItem.IsEnabled = ViewModel.CanBuild || ViewModel.Build.IsRunning;
        ToolTipService.SetToolTip(BuildNavItem, ViewModel.CanBuild ? null : ViewModel.BuildBlockedReason);
    }

    /// <summary>
    /// Reports the process's real elevation rather than asserting the manifest's intent.
    /// </summary>
    /// <remarks>
    /// A shipped build always requests administrator, so this normally just confirms it. It matters
    /// for the <c>-p:TinyWinElevation=false</c> development build, where a badge claiming elevation
    /// the process does not have would be a lie in the one place the user checks.
    /// </remarks>
    private void ShowElevationState()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        ElevationNote.Text = elevated
            ? "running as administrator"
            : "not elevated — imaging will fail";
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        await AppServices.LoadCatalogAsync();

        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        // Re-check the gate on every move, so a change made on Customize is reflected the moment
        // the user arrives anywhere else.
        RefreshGate();

        var page = Routes.FirstOrDefault(r => r.Tag == tag).Page;
        if (page is not null && ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }
    }

    private void OnBuildStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BuildViewModel.IsFinished) or nameof(BuildViewModel.IsRunning))
        {
            DoneNavItem.IsEnabled = ViewModel.Build.IsFinished;

            if (ViewModel.Build.IsFinished && ViewModel.Build.Succeeded)
            {
                ViewModel.Done.Refresh();
            }
        }
    }
}
