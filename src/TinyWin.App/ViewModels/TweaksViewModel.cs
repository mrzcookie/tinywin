using CommunityToolkit.Mvvm.ComponentModel;
using TinyWin.App.Services;
using TinyWin.Core.Abstractions;

namespace TinyWin.App.ViewModels;

/// <summary>
/// The Tweaks page: install-time settings rather than removals.
/// </summary>
/// <remarks>
/// A separate page on purpose. Everything here changes how Setup behaves and is reversible after
/// install; nothing here deletes anything. Keeping the two mental models apart is why the toggles
/// write to <see cref="UnattendOptions"/> and never to the component selection.
/// </remarks>
public sealed partial class TweaksViewModel : ObservableObject
{
    public TweaksViewModel(BuildSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
    }

    public BuildSession Session { get; }

    public bool AllowLocalAccount
    {
        get => Session.Tweaks.AllowLocalAccount;
        set => Update(o => o with { AllowLocalAccount = value });
    }

    public bool BypassTpmCheck
    {
        get => Session.Tweaks.BypassTpmCheck;
        set => Update(o => o with { BypassTpmCheck = value });
    }

    public bool BypassSecureBootCheck
    {
        get => Session.Tweaks.BypassSecureBootCheck;
        set => Update(o => o with { BypassSecureBootCheck = value });
    }

    public bool BypassRamCheck
    {
        get => Session.Tweaks.BypassRamCheck;
        set => Update(o => o with { BypassRamCheck = value });
    }

    public bool BypassCpuCheck
    {
        get => Session.Tweaks.BypassCpuCheck;
        set => Update(o => o with { BypassCpuCheck = value });
    }

    public bool BypassStorageCheck
    {
        get => Session.Tweaks.BypassStorageCheck;
        set => Update(o => o with { BypassStorageCheck = value });
    }

    public bool PreventDeviceEncryption
    {
        get => Session.Tweaks.PreventDeviceEncryption;
        set => Update(o => o with { PreventDeviceEncryption = value });
    }

    public bool SkipPrivacySettings
    {
        get => Session.Tweaks.SkipPrivacySettings;
        set => Update(o => o with { SkipPrivacySettings = value });
    }

    public bool SkipOemRegistration
    {
        get => Session.Tweaks.SkipOemRegistration;
        set => Update(o => o with { SkipOemRegistration = value });
    }

    public string EnabledSummary
    {
        get
        {
            var count = Descriptions().Count(d => d.Enabled);
            return count == 1 ? "1 tweak enabled" : $"{count} tweaks enabled";
        }
    }

    /// <summary>The Review page reads this so the two pages cannot describe the same toggle differently.</summary>
    public IReadOnlyList<(string Title, string Explanation, bool Enabled)> Descriptions() =>
    [
        ("Allow a local account",
            "Skips the \"you must sign in with a Microsoft account\" screen (the BypassNRO behaviour).",
            Session.Tweaks.AllowLocalAccount),
        ("Bypass the TPM 2.0 check",
            "Lets Setup install on hardware without a TPM.",
            Session.Tweaks.BypassTpmCheck),
        ("Bypass the Secure Boot check",
            "Lets Setup install on machines with Secure Boot unavailable or off.",
            Session.Tweaks.BypassSecureBootCheck),
        ("Bypass the 4 GB RAM check",
            "Lets Setup install on machines below the memory floor.",
            Session.Tweaks.BypassRamCheck),
        ("Bypass the CPU compatibility check",
            "Lets Setup install on processors not on Microsoft's supported list.",
            Session.Tweaks.BypassCpuCheck),
        ("Bypass the disk size check",
            "Lets Setup install on volumes below the 64 GB floor.",
            Session.Tweaks.BypassStorageCheck),
        ("Prevent automatic BitLocker encryption",
            "Stops Windows silently encrypting the system drive on first boot, which otherwise makes " +
            "recovery from another machine much harder.",
            Session.Tweaks.PreventDeviceEncryption),
        ("Skip the privacy questions",
            "Answers the OOBE privacy screens with everything off rather than prompting.",
            Session.Tweaks.SkipPrivacySettings),
        ("Skip OEM registration",
            "Removes the manufacturer registration step from OOBE.",
            Session.Tweaks.SkipOemRegistration),
    ];

    private void Update(Func<UnattendOptions, UnattendOptions> change)
    {
        Session.Tweaks = change(Session.Tweaks);
        OnPropertyChanged(string.Empty);
    }
}
