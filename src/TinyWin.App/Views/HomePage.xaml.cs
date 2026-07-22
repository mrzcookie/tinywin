using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TinyWin.App.Views;

/// <summary>
/// The landing page.
/// </summary>
/// <remarks>
/// Exists so that the first thing the app shows is not a screen with destructive checkboxes on it.
/// See the UI principles in docs/PLAN.md section 4.
/// </remarks>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();

        StepsRepeater.ItemsSource = new[]
        {
            "1 · Source — point TinyWin at a Windows 11 ISO and pick the edition to build from.",
            "2 · Customize — tick the components to remove. Each one shows its risk tier and exactly what it breaks.",
            "3 · Tweaks — optional setup behaviour: hardware check bypasses, local accounts, OOBE.",
            "4 · Review — read every action that will run, then unlock the build.",
            "5 · Build — watch the fourteen stages. Cancelling unwinds safely rather than leaving an image mounted.",
            "6 · Done — before and after sizes, and a count of every action that found no target.",
        };
    }

    private void OnGetStarted(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("source");
}
