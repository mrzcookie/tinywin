using Microsoft.UI.Xaml.Controls;
using TinyWin.App.Services;
using TinyWin.App.ViewModels;

namespace TinyWin.App.Views;

public sealed partial class TweaksPage : Page
{
    public TweaksPage()
    {
        InitializeComponent();
        ViewModel = AppServices.Shell.Tweaks;
    }

    public TweaksViewModel ViewModel { get; }
}
