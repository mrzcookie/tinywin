using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyWin.App.Services;
using TinyWin.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace TinyWin.App.Views;

public sealed partial class SourcePage : Page
{
    public SourcePage()
    {
        InitializeComponent();
        ViewModel = AppServices.Shell.Source;
    }

    public SourceViewModel ViewModel { get; }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;

        e.DragUIOverride.Caption = "Inspect this ISO";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0)
            {
                await ViewModel.InspectAsync(items[0].Path);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnNext(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("customize");
}
