using TinyWin.Core.Abstractions;

namespace TinyWin.Tests.Unattend;

/// <summary>One named input to the generator, and the architecture to render it for.</summary>
internal sealed record UnattendCase(string Name, string Architecture, UnattendOptions Options);

/// <summary>
/// The golden corpus. Every case here has a checked-in expected XML file of the same name under
/// <c>tests/TinyWin.Tests/Golden/Unattend</c>.
/// </summary>
/// <remarks>
/// Cases are chosen so that each option contributes to at least one file on its own — a
/// combined "everything" case alone would hide an option that quietly emits nothing, or that
/// emits something only because a neighbour was also set.
/// </remarks>
internal static class UnattendCases
{
    /// <summary>Every flag off. The floor the single-option cases are built on.</summary>
    internal static readonly UnattendOptions Nothing = new()
    {
        AllowLocalAccount = false,
        BypassTpmCheck = false,
        BypassSecureBootCheck = false,
        BypassRamCheck = false,
        BypassCpuCheck = false,
        BypassStorageCheck = false,
        PreventDeviceEncryption = false,
        SkipPrivacySettings = false,
        SkipOemRegistration = false,
    };

    private static readonly LocalAccountOptions Account = new()
    {
        Username = "tinywin",
        Password = "correct horse battery staple",
        DisplayName = "TinyWin User",
    };

    internal static IReadOnlyList<UnattendCase> All { get; } =
    [
        // The shipped defaults, on both supported architectures. These two files are the ones to
        // read first when reviewing a change.
        new("defaults-amd64", "amd64", new UnattendOptions()),
        new("defaults-arm64", "arm64", new UnattendOptions()),

        // Nothing selected must produce a file Setup treats as absent: no settings elements.
        new("nothing-amd64", "amd64", Nothing),

        // Install bypasses.
        new("bypasses-only-amd64", "amd64", Nothing with
        {
            BypassTpmCheck = true,
            BypassSecureBootCheck = true,
            BypassRamCheck = true,
            BypassCpuCheck = true,
            BypassStorageCheck = true,
        }),
        // A single bypass proves the RunSynchronous Order values renumber rather than leaving gaps.
        new("bypass-tpm-only-amd64", "amd64", Nothing with { BypassTpmCheck = true }),
        new("bypass-cpu-and-storage-only-arm64", "arm64", Nothing with
        {
            BypassCpuCheck = true,
            BypassStorageCheck = true,
        }),

        // OOBE behaviour, one option at a time.
        new("local-account-flag-only-amd64", "amd64", Nothing with { AllowLocalAccount = true }),
        new("privacy-and-oem-only-amd64", "amd64", Nothing with
        {
            SkipPrivacySettings = true,
            SkipOemRegistration = true,
        }),
        new("device-encryption-only-amd64", "amd64", Nothing with { PreventDeviceEncryption = true }),

        // Local accounts.
        new("local-account-amd64", "amd64", Nothing with
        {
            AllowLocalAccount = true,
            LocalAccount = Account,
        }),
        new("local-account-arm64", "arm64", Nothing with
        {
            AllowLocalAccount = true,
            LocalAccount = Account,
        }),
        new("local-account-no-password-amd64", "amd64", Nothing with
        {
            AllowLocalAccount = true,
            LocalAccount = new LocalAccountOptions { Username = "tinywin" },
        }),
        // Every XML metacharacter Setup could choke on, in the three places user text reaches
        // the document.
        new("local-account-escaping-amd64", "amd64", Nothing with
        {
            AllowLocalAccount = true,
            LocalAccount = new LocalAccountOptions
            {
                Username = "a&b",
                Password = "p<a>s&s\"w'd",
                DisplayName = "Ada & \"Bob\" <admin>",
            },
        }),

        // Language and time zone.
        new("language-only-amd64", "amd64", Nothing with { UiLanguage = "en-US" }),
        new("timezone-only-amd64", "amd64", Nothing with { TimeZone = "UTC" }),
        new("language-and-timezone-amd64", "amd64", Nothing with
        {
            UiLanguage = "de-DE",
            TimeZone = "W. Europe Standard Time",
        }),

        // Everything at once, on both architectures.
        new("everything-amd64", "amd64", new UnattendOptions
        {
            UiLanguage = "en-GB",
            TimeZone = "GMT Standard Time",
            LocalAccount = Account,
        }),
        new("everything-arm64", "arm64", new UnattendOptions
        {
            UiLanguage = "en-GB",
            TimeZone = "GMT Standard Time",
            LocalAccount = Account,
        }),
    ];

    internal static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var testCase in All)
            {
                data.Add(testCase.Name);
            }

            return data;
        }
    }

    internal static UnattendCase Get(string name) =>
        All.Single(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}
