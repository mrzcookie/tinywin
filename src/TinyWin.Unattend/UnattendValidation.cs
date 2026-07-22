using System.Buffers;
using System.Text.RegularExpressions;
using TinyWin.Core.Abstractions;

namespace TinyWin.Unattend;

/// <summary>
/// Input checks that run before any XML is produced.
/// </summary>
/// <remarks>
/// <para>
/// These throw rather than sanitise on purpose. A malformed locale or an illegal user name does
/// not break the XML — it breaks Windows Setup, minutes into an install, hours after the user
/// built the ISO. Failing at plan time is the only place the message can still be useful.
/// </para>
/// <para>
/// <b>Security:</b> no message produced here ever interpolates
/// <see cref="LocalAccountOptions.Password"/>. Exception messages reach logs and bug reports.
/// </para>
/// </remarks>
internal static partial class UnattendValidation
{
    /// <summary>SAM account names are capped at 20 characters.</summary>
    internal const int MaxUserNameLength = 20;

    /// <summary>Windows caps local account passwords at 127 characters.</summary>
    internal const int MaxPasswordLength = 127;

    internal const int MaxDisplayNameLength = 256;

    /// <summary>Longest in-box time zone id is well under this; the cap only stops absurd input.</summary>
    internal const int MaxTimeZoneLength = 128;

    /// <summary>
    /// Characters Windows rejects in a local account name, per the Local Users and Groups rules.
    /// </summary>
    private const string InvalidUserNameCharList = "\"/\\[]:;|=,+*?<>";

    private static readonly SearchValues<char> InvalidUserNameChars = SearchValues.Create(InvalidUserNameCharList);

    /// <summary>
    /// BCP-47-ish locale: a 2–3 letter language plus at least one subtag, e.g. <c>en-US</c>,
    /// <c>pt-BR</c>, <c>sr-Latn-RS</c>. The trailing subtag is required — Windows Setup wants a
    /// full locale, and a bare <c>en</c> silently falls back to the media default.
    /// </summary>
    [GeneratedRegex("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{1,8})+$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalePattern { get; }

    /// <summary>Returns the normalised architecture attribute value, or throws.</summary>
    internal static string NormalizeArchitecture(string architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);

        var trimmed = architecture.Trim();

        if (Matches(trimmed, "amd64") || Matches(trimmed, "x64") || Matches(trimmed, "x86_64"))
        {
            return UnattendSchema.Amd64;
        }

        if (Matches(trimmed, "arm64") || Matches(trimmed, "aarch64"))
        {
            return UnattendSchema.Arm64;
        }

        throw new ArgumentException(
            $"Unsupported processor architecture '{architecture}'. Windows 11 ships x64 and Arm64 media only, " +
            "so the architecture must be one of: amd64, x64, x86_64, arm64, aarch64.",
            nameof(architecture));

        static bool Matches(string value, string candidate) =>
            string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the locale unchanged, or null when none was requested.</summary>
    internal static string? ValidateLocale(string? locale, string propertyName)
    {
        if (locale is null)
        {
            return null;
        }

        if (!LocalePattern.IsMatch(locale))
        {
            throw new ArgumentException(
                $"'{locale}' is not a valid locale for {propertyName}. Expected a language and region such as 'en-US' or 'pt-BR'.",
                nameof(locale));
        }

        return locale;
    }

    /// <summary>Returns the time zone id unchanged, or null when none was requested.</summary>
    internal static string? ValidateTimeZone(string? timeZone)
    {
        if (timeZone is null)
        {
            return null;
        }

        // Windows time zone ids are registry key names under Time Zones, so a backslash is out,
        // and Setup matches them literally — leading or trailing space would never match.
        if (timeZone.Length == 0
            || timeZone.Length > MaxTimeZoneLength
            || timeZone.Trim().Length != timeZone.Length
            || timeZone.Contains('\\', StringComparison.Ordinal)
            || ContainsControlCharacter(timeZone))
        {
            throw new ArgumentException(
                $"'{timeZone}' is not a valid Windows time zone id. Expected an id such as 'UTC' or 'Pacific Standard Time'.",
                nameof(timeZone));
        }

        return timeZone;
    }

    /// <summary>
    /// Validates a local account. Returns null when no account was requested.
    /// </summary>
    internal static LocalAccountOptions? ValidateLocalAccount(LocalAccountOptions? account)
    {
        if (account is null)
        {
            return null;
        }

        ValidateUserName(account.Username);

        // Deliberately reports length and shape only. The password itself never reaches a message.
        if (account.Password is { } password)
        {
            if (password.Length > MaxPasswordLength)
            {
                throw new ArgumentException(
                    $"The local account password is {password.Length} characters. Windows accepts at most {MaxPasswordLength}.",
                    nameof(account));
            }

            if (ContainsControlCharacter(password))
            {
                throw new ArgumentException(
                    "The local account password contains a control character, which Windows Setup cannot read from an answer file.",
                    nameof(account));
            }
        }

        if (account.DisplayName is { Length: > MaxDisplayNameLength } displayName)
        {
            throw new ArgumentException(
                $"The local account display name is {displayName.Length} characters. The limit is {MaxDisplayNameLength}.",
                nameof(account));
        }

        if (account.DisplayName is { } name && ContainsControlCharacter(name))
        {
            throw new ArgumentException(
                "The local account display name contains a control character.",
                nameof(account));
        }

        return account;
    }

    private static void ValidateUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("The local account user name is empty.", nameof(userName));
        }

        if (userName.Length > MaxUserNameLength)
        {
            throw new ArgumentException(
                $"The local account user name '{userName}' is {userName.Length} characters. Windows accepts at most {MaxUserNameLength}.",
                nameof(userName));
        }

        if (userName.AsSpan().ContainsAny(InvalidUserNameChars) || ContainsControlCharacter(userName))
        {
            throw new ArgumentException(
                $"The local account user name '{userName}' contains a character Windows does not allow. "
                + "These are not allowed: \" / \\ [ ] : ; | = , + * ? < >",
                nameof(userName));
        }

        // Windows rejects a name made only of periods and spaces, and one that ends in a period.
        if (userName.EndsWith('.') || userName.All(c => c is '.' or ' '))
        {
            throw new ArgumentException(
                $"The local account user name '{userName}' is not allowed: it may not end in a period or consist only of periods and spaces.",
                nameof(userName));
        }
    }

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);
}
