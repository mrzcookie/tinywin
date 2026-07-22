namespace TinyWin.Core.Abstractions;

/// <summary>
/// Install-time options written into autounattend.xml. These are setup behaviours rather than
/// removals, which is why they live apart from the component catalog.
/// </summary>
public sealed record UnattendOptions
{
    /// <summary>Allows finishing setup without a Microsoft account (the BypassNRO behaviour).</summary>
    public bool AllowLocalAccount { get; init; } = true;

    public bool BypassTpmCheck { get; init; } = true;
    public bool BypassSecureBootCheck { get; init; } = true;
    public bool BypassRamCheck { get; init; } = true;
    public bool BypassCpuCheck { get; init; } = true;
    public bool BypassStorageCheck { get; init; } = true;

    /// <summary>Prevents automatic BitLocker device encryption on first boot.</summary>
    public bool PreventDeviceEncryption { get; init; } = true;

    public bool SkipPrivacySettings { get; init; } = true;
    public bool SkipOemRegistration { get; init; } = true;

    /// <summary>Null leaves the ISO's default. Otherwise a locale such as <c>en-US</c>.</summary>
    public string? UiLanguage { get; init; }

    public string? TimeZone { get; init; }

    /// <summary>Optional local account created during setup. Null means prompt as normal.</summary>
    public LocalAccountOptions? LocalAccount { get; init; }
}

public sealed record LocalAccountOptions
{
    public required string Username { get; init; }

    /// <summary>
    /// Null means no password. Deliberately not persisted to any preset file — a saved preset
    /// containing a password would end up in a public gist within a week.
    /// </summary>
    public string? Password { get; init; }

    public string? DisplayName { get; init; }
}

public interface IUnattendGenerator
{
    /// <summary>Renders autounattend.xml. Pure and deterministic, so it is golden-testable.</summary>
    string Generate(UnattendOptions options, string architecture);
}
