using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyWin.App.Services;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;

namespace TinyWin.App.ViewModels;

/// <summary>One selectable edition card.</summary>
public sealed class EditionCard(ImageEdition edition)
{
    public ImageEdition Edition { get; } = edition;

    public string Name => Edition.Name;

    public string IndexText => $"Index {Edition.Index}";

    public string SizeText => Formatting.Bytes(Edition.SizeBytes);

    public string Details =>
        $"{Edition.Architecture}  ·  build {Edition.Version.Build}.{Edition.Version.Revision}" +
        (string.IsNullOrEmpty(Edition.DefaultLanguage) ? string.Empty : $"  ·  {Edition.DefaultLanguage}");

    /// <summary>
    /// What a screen reader announces for the card. Without this the GridView container falls back
    /// to the type name and reads out "TinyWin.App.ViewModels.EditionCard".
    /// </summary>
    public override string ToString() => $"{Name}, {IndexText}, {SizeText}";
}

/// <summary>
/// The Source page: choose an ISO, inspect it, pick an edition.
/// </summary>
public sealed partial class SourceViewModel : ObservableObject
{
    private readonly IImagingBackend _imaging;

    public SourceViewModel(BuildSession session, IImagingBackend imaging)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(imaging);

        Session = session;
        _imaging = imaging;
        IsoPath = string.Empty;
        ErrorMessage = string.Empty;
    }

    public BuildSession Session { get; }

    public ObservableCollection<EditionCard> Editions { get; } = [];

    [ObservableProperty]
    public partial string IsoPath { get; set; }

    [ObservableProperty]
    public partial bool IsInspecting { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial EditionCard? SelectedEdition { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasEditions => Editions.Count > 0;

    public string SourceSizeText => Session.Image is { } image ? Formatting.Bytes(image.TotalSizeBytes) : "—";

    public string BuildText => Session.Edition is { } edition
        ? $"Build {edition.Version.Build}.{edition.Version.Revision}"
        : "—";

    public string ArchitectureText => Session.Edition?.Architecture ?? "—";

    public string LanguageText => Session.Edition?.DefaultLanguage ?? "—";

    public MediaSupport MediaSupport => Session.MediaSupport;

    public bool ShowUnverifiedWarning => HasEditions && MediaSupport == MediaSupport.Unverified;

    public bool ShowUnsupportedError => HasEditions && MediaSupport == MediaSupport.Unsupported;

    public string MediaSupportText => MediaSupport switch
    {
        MediaSupport.Supported => "This media is on the 24H2 / 25H2 servicing branch the catalog is validated against.",
        MediaSupport.Unverified =>
            "This build is newer than the catalog has been validated against. TinyWin will still build it, but some " +
            "removals may find no target — the build report will list every one that does.",
        _ => "This build is older than 24H2 (26100) and is past end of updates. TinyWin does not modify it.",
    };

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await FilePickers.PickIsoAsync();
        if (path is not null)
        {
            await InspectAsync(path);
        }
    }

    /// <summary>
    /// Inspects a candidate ISO and populates the edition list.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="IImagingBackend"/> and nothing else. Today that is the demo backend;
    /// when the real one lands this method does not change.
    /// </remarks>
    public async Task InspectAsync(string path)
    {
        ErrorMessage = string.Empty;
        Editions.Clear();
        Session.Image = null;
        Session.Edition = null;
        SelectedEdition = null;
        RefreshSummary();

        if (!File.Exists(path))
        {
            ErrorMessage = $"'{path}' does not exist.";
            OnPropertyChanged(nameof(HasError));
            return;
        }

        if (!string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "TinyWin needs a Windows 11 ISO. Drop the .iso file itself, not a folder or a WIM.";
            OnPropertyChanged(nameof(HasError));
            return;
        }

        IsoPath = path;
        IsInspecting = true;

        try
        {
            var editions = await _imaging.GetEditionsAsync(path);
            var info = new WindowsImageInfo
            {
                SourceIsoPath = path,
                IsEsd = false,
                Editions = editions,
                TotalSizeBytes = new FileInfo(path).Length,
            };

            Session.Image = info;

            foreach (var edition in editions)
            {
                Editions.Add(new EditionCard(edition));
            }

            // Pro rather than the first index: it is what most people building a lab image want, and
            // nothing destructive follows from the default.
            SelectedEdition = Editions.FirstOrDefault(e =>
                e.Edition.EditionId.Equals("Professional", StringComparison.OrdinalIgnoreCase)) ?? Editions.FirstOrDefault();

            if (Session.OutputIsoPath.Length == 0)
            {
                Session.OutputIsoPath = Path.Combine(
                    Path.GetDirectoryName(path) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(path) + "-tiny.iso");
            }
        }
        catch (IOException ex)
        {
            ErrorMessage = $"Could not read that ISO: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            ErrorMessage = $"Could not read that ISO: {ex.Message}";
        }
        finally
        {
            IsInspecting = false;
            RefreshSummary();
        }
    }

    partial void OnSelectedEditionChanged(EditionCard? value)
    {
        Session.Edition = value?.Edition;
        RefreshSummary();
    }

    partial void OnErrorMessageChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasError));
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasEditions));
        OnPropertyChanged(nameof(SourceSizeText));
        OnPropertyChanged(nameof(BuildText));
        OnPropertyChanged(nameof(ArchitectureText));
        OnPropertyChanged(nameof(LanguageText));
        OnPropertyChanged(nameof(MediaSupport));
        OnPropertyChanged(nameof(MediaSupportText));
        OnPropertyChanged(nameof(ShowUnverifiedWarning));
        OnPropertyChanged(nameof(ShowUnsupportedError));
    }
}
