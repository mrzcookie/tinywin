using Microsoft.Windows.Storage.Pickers;

namespace TinyWin.App.Services;

/// <summary>
/// File dialogs.
/// </summary>
/// <remarks>
/// Uses the Windows App SDK pickers rather than the WinRT ones: the WinRT pickers need an HWND
/// injected by hand in an unpackaged app, and they behave badly under elevation. These take a
/// <c>WindowId</c> directly.
/// </remarks>
public static class FilePickers
{
    public static async Task<string?> PickIsoAsync()
    {
        if (AppServices.MainWindow is not { } window)
        {
            return null;
        }

        var picker = new FileOpenPicker(window.AppWindow.Id)
        {
            CommitButtonText = "Use this ISO",
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".iso");

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public static async Task<string?> PickSaveAsync(string suggestedName, string extension, string typeLabel)
    {
        if (AppServices.MainWindow is not { } window)
        {
            return null;
        }

        var picker = new FileSavePicker(window.AppWindow.Id)
        {
            SuggestedFileName = suggestedName,
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(typeLabel, [extension]);

        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public static async Task<string?> PickOpenAsync(string extension)
    {
        if (AppServices.MainWindow is not { } window)
        {
            return null;
        }

        var picker = new FileOpenPicker(window.AppWindow.Id);
        picker.FileTypeFilter.Add(extension);

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }
}
