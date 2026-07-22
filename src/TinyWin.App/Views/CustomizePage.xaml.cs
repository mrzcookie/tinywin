using Microsoft.UI.Xaml.Controls;
using TinyWin.App.Services;
using TinyWin.App.ViewModels;

namespace TinyWin.App.Views;

public sealed partial class CustomizePage : Page
{
    public CustomizePage()
    {
        InitializeComponent();
        ViewModel = AppServices.Shell.Customize;
    }

    public CustomizeViewModel ViewModel { get; }
}
