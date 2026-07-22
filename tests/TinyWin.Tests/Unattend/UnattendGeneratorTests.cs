using System.Globalization;
using System.Xml.Linq;
using TinyWin.Core.Abstractions;
using TinyWin.Unattend;

namespace TinyWin.Tests.Unattend;

/// <summary>
/// Structural assertions about the generated answer file.
/// </summary>
/// <remarks>
/// The golden files prove the output does not change by accident. These prove it is right in the
/// first place: correct component identity tuples, correct passes, contiguous command ordering,
/// and inputs rejected at plan time rather than at install time.
/// </remarks>
public sealed class UnattendGeneratorTests
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    private static readonly UnattendOptions Nothing = UnattendCases.Nothing;

    private static string Render(UnattendOptions options, string architecture = "amd64") =>
        new UnattendGenerator().Generate(options, architecture);

    private static XDocument Parse(UnattendOptions options, string architecture = "amd64") =>
        XDocument.Parse(Render(options, architecture));

    // -------------------------------------------------------------------------------------
    // Document shape
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Root_is_unattend_in_the_unattend_namespace()
    {
        var document = Parse(new UnattendOptions());

        Assert.Equal(Ns + "unattend", document.Root!.Name);
    }

    [Fact]
    public void Declares_utf8_encoding()
    {
        // A declaration of utf-16 over utf-8 bytes is the classic way an answer file is silently
        // ignored, so this is worth its own test rather than only living in the golden files.
        var document = Parse(new UnattendOptions());

        Assert.Equal("utf-8", document.Declaration!.Encoding);
        Assert.Equal("1.0", document.Declaration.Version);
    }

    [Fact]
    public void Does_not_begin_with_a_byte_order_mark()
    {
        var xml = Render(new UnattendOptions());

        // A BOM ahead of the declaration is tolerated by some parsers and not others; the safe
        // answer file starts with '<'.
        Assert.Equal('<', xml[0]);
        Assert.StartsWith("<?xml", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Uses_crlf_line_endings_throughout()
    {
        var xml = Render(UnattendCases.Get("everything-amd64").Options);

        var withoutCrlf = xml.Replace("\r\n", string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain('\r', withoutCrlf);
        Assert.DoesNotContain('\n', withoutCrlf);
    }

    [Fact]
    public void Is_deterministic()
    {
        var options = UnattendCases.Get("everything-arm64").Options;
        var generator = new UnattendGenerator();

        Assert.Equal(generator.Generate(options, "arm64"), generator.Generate(options, "arm64"));
        Assert.Equal(generator.Generate(options, "arm64"), new UnattendGenerator().Generate(options, "arm64"));
    }

    [Fact]
    public void Numbers_are_rendered_with_invariant_digits()
    {
        // The solution sets InvariantGlobalization, so a culture-swapping test cannot run here.
        // What it would have caught is a native-digit Order or ProtectYourPC value, so assert that
        // directly instead.
        var xml = Render(UnattendCases.Get("everything-amd64").Options);

        var document = XDocument.Parse(xml);
        var numbers = document.Descendants(Ns + "Order")
            .Concat(document.Descendants(Ns + "ProtectYourPC"))
            .Select(e => e.Value)
            .ToList();

        Assert.NotEmpty(numbers);
        Assert.All(numbers, value => Assert.All(value, c => Assert.InRange(c, '0', '9')));
    }

    [Fact]
    public void Nothing_selected_produces_no_settings_at_all()
    {
        var document = Parse(Nothing);

        Assert.Empty(document.Root!.Elements(Ns + "settings"));
    }

    [Fact]
    public void Passes_appear_in_the_order_setup_runs_them()
    {
        var document = Parse(new UnattendOptions { UiLanguage = "en-US" });

        var passes = document.Root!.Elements(Ns + "settings")
            .Select(s => (string)s.Attribute("pass")!)
            .ToList();

        string[] expected = ["windowsPE", "specialize", "oobeSystem"];
        Assert.Equal(expected, passes);
    }

    // -------------------------------------------------------------------------------------
    // Component identity
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("amd64", "amd64")]
    [InlineData("AMD64", "amd64")]
    [InlineData("x64", "amd64")]
    [InlineData("X64", "amd64")]
    [InlineData("x86_64", "amd64")]
    [InlineData(" amd64 ", "amd64")]
    [InlineData("arm64", "arm64")]
    [InlineData("ARM64", "arm64")]
    [InlineData("Arm64", "arm64")]
    [InlineData("aarch64", "arm64")]
    public void Architecture_aliases_normalise_to_the_schema_spelling(string input, string expected)
    {
        var document = Parse(new UnattendOptions { UiLanguage = "en-US" }, input);

        var architectures = document.Descendants(Ns + "component")
            .Select(c => (string)c.Attribute("processorArchitecture")!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string[] expectedArchitectures = [expected];
        Assert.Equal(expectedArchitectures, architectures);
    }

    [Theory]
    [InlineData("x86")]          // Windows 11 ships no 32-bit media
    [InlineData("arm")]          // 32-bit Arm, likewise
    [InlineData("ia64")]
    [InlineData("amd 64")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Unsupported_architectures_are_rejected(string? architecture)
    {
        Assert.ThrowsAny<ArgumentException>(() => Render(new UnattendOptions(), architecture!));
    }

    [Theory]
    [InlineData("amd64")]
    [InlineData("arm64")]
    public void Every_component_carries_the_microsoft_identity_attributes(string architecture)
    {
        var document = Parse(UnattendCases.Get("everything-amd64").Options, architecture);

        var components = document.Descendants(Ns + "component").ToList();
        Assert.NotEmpty(components);

        foreach (var component in components)
        {
            Assert.Equal("31bf3856ad364e35", (string)component.Attribute("publicKeyToken")!);
            Assert.Equal("neutral", (string)component.Attribute("language")!);
            Assert.Equal("nonSxS", (string)component.Attribute("versionScope")!);
            Assert.Equal(architecture, (string)component.Attribute("processorArchitecture")!);
            Assert.NotNull(component.Attribute("name"));
        }
    }

    [Fact]
    public void Each_pass_uses_the_component_that_is_valid_for_it()
    {
        // The -WinPE international component is only valid in windowsPE, and the plain one is
        // only valid outside it. Swapping them yields a file Setup ignores without complaint.
        var document = Parse(UnattendCases.Get("everything-amd64").Options);

        string[] windowsPe = ["Microsoft-Windows-International-Core-WinPE", "Microsoft-Windows-Setup"];
        string[] specialize = ["Microsoft-Windows-Deployment"];
        string[] oobeSystem = ["Microsoft-Windows-International-Core", "Microsoft-Windows-Shell-Setup"];

        Assert.Equal(windowsPe, ComponentNames(document, "windowsPE"));
        Assert.Equal(specialize, ComponentNames(document, "specialize"));
        Assert.Equal(oobeSystem, ComponentNames(document, "oobeSystem"));
    }

    // -------------------------------------------------------------------------------------
    // Install bypasses
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(UnattendOptions.BypassTpmCheck), "BypassTPMCheck")]
    [InlineData(nameof(UnattendOptions.BypassSecureBootCheck), "BypassSecureBootCheck")]
    [InlineData(nameof(UnattendOptions.BypassRamCheck), "BypassRAMCheck")]
    [InlineData(nameof(UnattendOptions.BypassCpuCheck), "BypassCPUCheck")]
    [InlineData(nameof(UnattendOptions.BypassStorageCheck), "BypassStorageCheck")]
    public void Each_bypass_flag_writes_exactly_its_own_labconfig_value(string flag, string valueName)
    {
        var options = flag switch
        {
            nameof(UnattendOptions.BypassTpmCheck) => Nothing with { BypassTpmCheck = true },
            nameof(UnattendOptions.BypassSecureBootCheck) => Nothing with { BypassSecureBootCheck = true },
            nameof(UnattendOptions.BypassRamCheck) => Nothing with { BypassRamCheck = true },
            nameof(UnattendOptions.BypassCpuCheck) => Nothing with { BypassCpuCheck = true },
            nameof(UnattendOptions.BypassStorageCheck) => Nothing with { BypassStorageCheck = true },
            _ => throw new ArgumentOutOfRangeException(nameof(flag)),
        };

        var paths = CommandPaths(Parse(options), "windowsPE");

        string[] expected = [$"reg.exe add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v {valueName} /t REG_DWORD /d 1 /f"];
        Assert.Equal(expected, paths);
    }

    [Fact]
    public void Bypasses_are_written_in_the_windows_pe_pass()
    {
        // LabConfig is read by the compatibility check while Setup is still in WinPE. Writing it
        // in specialize would be far too late.
        var document = Parse(Nothing with { BypassTpmCheck = true });

        Assert.DoesNotContain(
            document.Root!.Elements(Ns + "settings"),
            settings => (string)settings.Attribute("pass")! != "windowsPE");
    }

    [Fact]
    public void No_bypass_flags_means_no_setup_component()
    {
        var document = Parse(Nothing with { SkipOemRegistration = true });

        Assert.DoesNotContain(
            document.Descendants(Ns + "component"),
            component => (string)component.Attribute("name")! == "Microsoft-Windows-Setup");
    }

    // -------------------------------------------------------------------------------------
    // specialize registry writes
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Preventing_device_encryption_writes_the_bitlocker_policy()
    {
        var paths = CommandPaths(Parse(Nothing with { PreventDeviceEncryption = true }), "specialize");

        string[] expected = ["reg.exe add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\BitLocker\" /v PreventDeviceEncryption /t REG_DWORD /d 1 /f"];
        Assert.Equal(expected, paths);
    }

    [Fact]
    public void Skipping_privacy_settings_writes_the_policy_and_turns_off_express_settings()
    {
        var document = Parse(Nothing with { SkipPrivacySettings = true });

        string[] expected = ["reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\OOBE\" /v DisablePrivacyExperience /t REG_DWORD /d 1 /f"];
        Assert.Equal(expected, CommandPaths(document, "specialize"));

        // 3 is the only documented ProtectYourPC value that turns Express settings off.
        Assert.Equal("3", (string)document.Descendants(Ns + "ProtectYourPC").Single());
    }

    [Fact]
    public void Allowing_a_local_account_sets_bypassnro_and_hides_the_sign_in_screens()
    {
        var document = Parse(Nothing with { AllowLocalAccount = true });

        string[] expected = ["reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE\" /v BypassNRO /t REG_DWORD /d 1 /f"];
        Assert.Equal(expected, CommandPaths(document, "specialize"));

        Assert.Equal("true", (string)document.Descendants(Ns + "HideOnlineAccountScreens").Single());
    }

    [Fact]
    public void Disallowing_a_local_account_leaves_the_sign_in_screens_alone()
    {
        var document = Parse(Nothing with { AllowLocalAccount = false });

        Assert.Empty(document.Descendants(Ns + "HideOnlineAccountScreens"));
        Assert.DoesNotContain("BypassNRO", Render(Nothing), StringComparison.Ordinal);
    }

    [Fact]
    public void Skipping_oem_registration_hides_the_registration_screen()
    {
        var document = Parse(Nothing with { SkipOemRegistration = true });

        Assert.Equal("true", (string)document.Descendants(Ns + "HideOEMRegistrationScreen").Single());
    }

    // -------------------------------------------------------------------------------------
    // Command ordering
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Run_synchronous_orders_are_contiguous_from_one_in_every_case()
    {
        foreach (var testCase in UnattendCases.All)
        {
            var document = Parse(testCase.Options, testCase.Architecture);

            foreach (var list in document.Descendants(Ns + "RunSynchronous"))
            {
                var orders = list.Elements(Ns + "RunSynchronousCommand")
                    .Select(c => int.Parse((string)c.Element(Ns + "Order")!, CultureInfo.InvariantCulture))
                    .ToList();

                Assert.Equal(Enumerable.Range(1, orders.Count), orders);
            }
        }
    }

    [Fact]
    public void Every_run_synchronous_command_is_an_add_action()
    {
        var document = Parse(UnattendCases.Get("everything-amd64").Options);

        var commands = document.Descendants(Ns + "RunSynchronousCommand").ToList();
        Assert.NotEmpty(commands);

        foreach (var command in commands)
        {
            Assert.Equal("add", (string)command.Attribute(Wcm + "action")!);
            Assert.NotNull(command.Element(Ns + "Path"));
            Assert.NotNull(command.Element(Ns + "Description"));
        }
    }

    // -------------------------------------------------------------------------------------
    // Local accounts
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Local_account_is_created_as_an_administrator()
    {
        var document = Parse(Nothing with
        {
            LocalAccount = new LocalAccountOptions { Username = "zach", DisplayName = "Zach" },
        });

        var account = document.Descendants(Ns + "LocalAccount").Single();

        Assert.Equal("add", (string)account.Attribute(Wcm + "action")!);
        Assert.Equal("zach", (string)account.Element(Ns + "Name")!);
        Assert.Equal("Administrators", (string)account.Element(Ns + "Group")!);
        Assert.Equal("Zach", (string)account.Element(Ns + "DisplayName")!);
    }

    [Fact]
    public void Local_account_without_a_password_gets_an_explicit_empty_one()
    {
        // An empty Value is what stops OOBE asking for a password. Omitting the element entirely
        // is only conventionally read the same way.
        var document = Parse(Nothing with
        {
            LocalAccount = new LocalAccountOptions { Username = "zach" },
        });

        var password = document.Descendants(Ns + "Password").Single();

        Assert.Equal(string.Empty, (string)password.Element(Ns + "Value")!);
        Assert.Equal("true", (string)password.Element(Ns + "PlainText")!);
        Assert.Empty(document.Descendants(Ns + "DisplayName"));
    }

    [Fact]
    public void Password_is_written_once_as_plain_text()
    {
        const string password = "s3cret-pass";

        var xml = Render(Nothing with
        {
            LocalAccount = new LocalAccountOptions { Username = "zach", Password = password },
        });

        var document = XDocument.Parse(xml);
        var element = document.Descendants(Ns + "Password").Single();

        Assert.Equal(password, (string)element.Element(Ns + "Value")!);
        Assert.Equal("true", (string)element.Element(Ns + "PlainText")!);

        // Exactly one occurrence in the whole document — no stray copy in a comment or command.
        Assert.Equal(1, CountOccurrences(xml, password));
    }

    [Fact]
    public void No_account_means_no_password_anywhere()
    {
        var xml = Render(new UnattendOptions());

        Assert.DoesNotContain("Password", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalAccount", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_metacharacters_in_user_supplied_text_survive_a_round_trip()
    {
        const string password = "p<a>s&s\"w'd";
        const string displayName = "Ada & \"Bob\" <admin>";

        var document = Parse(Nothing with
        {
            LocalAccount = new LocalAccountOptions
            {
                Username = "a&b",
                Password = password,
                DisplayName = displayName,
            },
        });

        var account = document.Descendants(Ns + "LocalAccount").Single();

        Assert.Equal("a&b", (string)account.Element(Ns + "Name")!);
        Assert.Equal(displayName, (string)account.Element(Ns + "DisplayName")!);
        Assert.Equal(password, (string)account.Element(Ns + "Password")!.Element(Ns + "Value")!);
    }

    // -------------------------------------------------------------------------------------
    // Security: the password must not leak out of the XML
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("this-password-is-far-too-long-to-be-accepted-by-windows-and-should-be-rejected-without-ever-being-repeated-back-to-the-caller-abcdef")]
    [InlineData("has\ta\ttab")]
    public void A_rejected_password_never_appears_in_the_error_message(string password)
    {
        var options = Nothing with
        {
            LocalAccount = new LocalAccountOptions { Username = "zach", Password = password },
        };

        var exception = Assert.ThrowsAny<ArgumentException>(() => Render(options));

        // Exception messages end up in logs, bug reports and pasted stack traces.
        Assert.DoesNotContain(password, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_containing_a_control_character_is_rejected_without_being_echoed()
    {
        var password = "bell" + (char)7 + "inside";

        var options = Nothing with
        {
            LocalAccount = new LocalAccountOptions { Username = "zach", Password = password },
        };

        var exception = Assert.ThrowsAny<ArgumentException>(() => Render(options));

        Assert.DoesNotContain(password, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_accepted_password_appears_only_inside_the_password_value_element()
    {
        const string password = "unique-sentinel-value-9f2a";

        var document = Parse(UnattendCases.Get("everything-amd64").Options with
        {
            LocalAccount = new LocalAccountOptions { Username = "zach", Password = password },
        });

        var carriers = document.Descendants()
            .Where(e => !e.HasElements && string.Equals(e.Value, password, StringComparison.Ordinal))
            .Select(e => e.Name.LocalName)
            .ToList();

        string[] expected = ["Value"];
        Assert.Equal(expected, carriers);
    }

    // -------------------------------------------------------------------------------------
    // Input validation
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("way-too-long-a-user-name")]
    [InlineData("bad\\name")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    [InlineData("bad;name")]
    [InlineData("bad|name")]
    [InlineData("bad=name")]
    [InlineData("bad,name")]
    [InlineData("bad+name")]
    [InlineData("bad*name")]
    [InlineData("bad?name")]
    [InlineData("bad<name")]
    [InlineData("bad>name")]
    [InlineData("bad[name")]
    [InlineData("bad]name")]
    [InlineData("bad\"name")]
    [InlineData("trailing.")]
    [InlineData("...")]
    [InlineData("tab\there")]
    public void Invalid_user_names_are_rejected(string username)
    {
        var options = Nothing with { LocalAccount = new LocalAccountOptions { Username = username } };

        Assert.ThrowsAny<ArgumentException>(() => Render(options));
    }

    [Theory]
    [InlineData("zach")]
    [InlineData("Zach Cookie")]
    [InlineData("a")]
    [InlineData("twenty-characters-ok")]
    [InlineData("user.name")]
    [InlineData("user@example")]
    [InlineData("a&b")]
    [InlineData("Ünicode")]
    public void Valid_user_names_are_accepted(string username)
    {
        var options = Nothing with { LocalAccount = new LocalAccountOptions { Username = username } };

        var document = Parse(options);

        Assert.Equal(username, (string)document.Descendants(Ns + "Name").Single());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en_US")]
    [InlineData("English")]
    [InlineData("en US")]
    [InlineData("")]
    [InlineData("--")]
    [InlineData("en-")]
    public void Invalid_ui_languages_are_rejected(string language)
    {
        Assert.ThrowsAny<ArgumentException>(() => Render(Nothing with { UiLanguage = language }));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("pt-BR")]
    [InlineData("sr-Latn-RS")]
    [InlineData("es-419")]
    [InlineData("zh-Hans-CN")]
    public void Valid_ui_languages_are_accepted(string language)
    {
        var document = Parse(Nothing with { UiLanguage = language });

        Assert.Equal(
            language,
            (string)document.Descendants(Ns + "SetupUILanguage").Single().Element(Ns + "UILanguage")!);
    }

    [Fact]
    public void Ui_language_seeds_both_the_setup_ui_and_the_installed_system()
    {
        var document = Parse(Nothing with { UiLanguage = "de-DE" });

        var winPe = ComponentIn(document, "windowsPE", "Microsoft-Windows-International-Core-WinPE");
        var oobe = ComponentIn(document, "oobeSystem", "Microsoft-Windows-International-Core");

        string[] shared = ["InputLocale", "SystemLocale", "UILanguage", "UserLocale"];

        foreach (var name in shared)
        {
            Assert.Equal("de-DE", (string)winPe.Element(Ns + name)!);
            Assert.Equal("de-DE", (string)oobe.Element(Ns + name)!);
        }

        // SetupUILanguage is windowsPE-only; it has no meaning in oobeSystem.
        Assert.NotNull(winPe.Element(Ns + "SetupUILanguage"));
        Assert.Null(oobe.Element(Ns + "SetupUILanguage"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" UTC")]
    [InlineData("UTC ")]
    [InlineData("Bad\\Zone")]
    [InlineData("tab\there")]
    public void Invalid_time_zones_are_rejected(string timeZone)
    {
        Assert.ThrowsAny<ArgumentException>(() => Render(Nothing with { TimeZone = timeZone }));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Pacific Standard Time")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("GMT Standard Time")]
    public void Valid_time_zones_reach_the_shell_setup_component(string timeZone)
    {
        var document = Parse(Nothing with { TimeZone = timeZone });

        var shellSetup = ComponentIn(document, "oobeSystem", "Microsoft-Windows-Shell-Setup");

        Assert.Equal(timeZone, (string)shellSetup.Element(Ns + "TimeZone")!);
    }

    [Fact]
    public void Null_options_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new UnattendGenerator().Generate(null!, "amd64"));
    }

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    private static IEnumerable<string> ComponentNames(XDocument document, string pass) =>
        Settings(document, pass).Elements(Ns + "component").Select(c => (string)c.Attribute("name")!);

    private static XElement ComponentIn(XDocument document, string pass, string componentName) =>
        Settings(document, pass).Elements(Ns + "component")
            .Single(c => string.Equals((string)c.Attribute("name")!, componentName, StringComparison.Ordinal));

    private static XElement Settings(XDocument document, string pass) =>
        document.Root!.Elements(Ns + "settings")
            .Single(s => string.Equals((string)s.Attribute("pass")!, pass, StringComparison.Ordinal));

    private static List<string> CommandPaths(XDocument document, string pass) =>
        Settings(document, pass)
            .Descendants(Ns + "RunSynchronousCommand")
            .Select(c => (string)c.Element(Ns + "Path")!)
            .ToList();

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
