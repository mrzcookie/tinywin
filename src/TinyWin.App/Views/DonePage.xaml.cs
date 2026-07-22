using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TinyWin.App.Services;
using TinyWin.App.ViewModels;

namespace TinyWin.App.Views;

public sealed partial class DonePage : Page
{
    public DonePage()
    {
        InitializeComponent();
        ViewModel = AppServices.Shell.Done;
    }

    public DoneViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    private void OnViewLog(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("build");
}
