using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;
using TinyWin.App.Services;
using TinyWin.App.ViewModels;

namespace TinyWin.App.Views;

public sealed partial class BuildPage : Page
{
    public BuildPage()
    {
        InitializeComponent();
        ViewModel = AppServices.Shell.Build;

        // Tail the log while "Follow" is on. Scrolling is a view concern, so it lives here rather
        // than in the view model.
        ((INotifyCollectionChanged)ViewModel.Log).CollectionChanged += OnLogChanged;
        Unloaded += (_, _) => ((INotifyCollectionChanged)ViewModel.Log).CollectionChanged -= OnLogChanged;
    }

    public BuildViewModel ViewModel { get; }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || AutoScrollToggle.IsChecked != true)
        {
            return;
        }

        if (ViewModel.Log.Count > 0)
        {
            LogList.ScrollIntoView(ViewModel.Log[^1]);
        }
    }
}
