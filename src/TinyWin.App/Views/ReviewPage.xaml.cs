using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TinyWin.App.Services;
using TinyWin.App.ViewModels;

namespace TinyWin.App.Views;

public sealed partial class ReviewPage : Page
{
    public ReviewPage()
    {
        InitializeComponent();
        ViewModel = AppServices.Shell.Review;
    }

    public ReviewViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    // NavigateTo re-runs the gate before it moves, so a stale enabled state cannot let this through.
    private void OnGoToBuild(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("build");
}
