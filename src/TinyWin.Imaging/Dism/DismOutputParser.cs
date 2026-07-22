using System.Globalization;
using System.Text.RegularExpressions;
using TinyWin.Core.Abstractions;
using TinyWin.Core.Models;

namespace TinyWin.Imaging.Dism;

/// <summary>The state DISM reports for a capability, feature or package.</summary>
public enum DismComponentState
{
    /// <summary>DISM did not list the component at all — the catalog entry has drifted.</summary>
    Absent,
    Installed,
    Enabled,
    Disabled,
    DisabledWithPayloadRemoved,
    Superseded,
    Staged,
    /// <summary>Listed, but with a state string we do not recognise. Treated as present.</summary>
    Other,
}

/// <summary>
/// Parses <c>dism.exe</c> output. Pure functions over strings — no process, no elevation.
/// </summary>
/// <remarks>
/// This is the other half of the testable seam (see <see cref="DismCommandLine"/>). Everything here
/// is exercised against captured real DISM output under <c>Samples/</c> in the test project, which
/// is the only way to get confidence in this code on an unelevated machine.
///
/// <para>All key matching assumes <c>/English</c> was passed. It always is —
/// <see cref="DismCommandLine"/> has no code path that omits it.</para>
/// </remarks>
public static partial class DismOutputParser
{
    private const string IndexKey = "Index";
    private const string PackageNameKey = "PackageName";
    private const string MountDirKey = "Mount Dir";
    private const string CapabilityIdentityKey = "Capability Identity";
    private const string FeatureNameKey = "Feature Name";
    private const string PackageIdentityKey = "Package Identity";
    private const string StateKey = "State";

    /// <summary>
    /// Splits DISM output into <c>Key : Value</c> blocks, keeping only those carrying
    /// <paramref name="discriminator"/>.
    /// </summary>
    /// <remarks>
    /// The discriminator is what discards DISM's banner ("Deployment Image Servicing and Management
    /// tool" / "Image Version: ...") and trailing prose without maintaining a list of things to
    /// ignore. Note the banner's <c>Version:</c> has no space before the colon, while every real
    /// field uses <c>" : "</c> — so the banner does not even parse as fields.
    /// </remarks>
    public static IReadOnlyList<DismRecord> ParseRecords(string output, string discriminator)
    {
        ArgumentNullException.ThrowIfNull(output);

        var records = new List<DismRecord>();
        var current = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? lastKey = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');

            if (line.Length == 0)
            {
                Commit();
                continue;
            }

            if (TrySplitField(line, out var key, out var value))
            {
                if (!current.TryGetValue(key, out var values))
                {
                    values = [];
                    current[key] = values;
                }

                values.Add(value);
                lastKey = key;
                continue;
            }

            // An indented line with no separator continues the previous field. This is how DISM
            // renders the language list under "Languages :".
            if (lastKey is not null && (rawLine.StartsWith(' ') || rawLine.StartsWith('\t')))
            {
                current[lastKey].Add(line.Trim());
            }
        }

        Commit();
        return records;

        void Commit()
        {
            if (current.ContainsKey(discriminator))
            {
                records.Add(new DismRecord(current));
            }

            current = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            lastKey = null;
        }
    }

    /// <summary>
    /// Splits on the <i>first</i> <c>" : "</c>. Values legitimately contain colons — <c>Install
    /// Time : 12/2/2025 3:21:15 PM</c> — but never one surrounded by spaces.
    /// </summary>
    private static bool TrySplitField(string line, out string key, out string value)
    {
        var separator = line.IndexOf(" : ", StringComparison.Ordinal);
        if (separator >= 0)
        {
            key = line[..separator].Trim();
            value = line[(separator + 3)..].Trim();
            return key.Length > 0;
        }

        // "ResourceId :" — a present but empty field. Trailing whitespace is already gone.
        if (line.EndsWith(" :", StringComparison.Ordinal))
        {
            key = line[..^2].Trim();
            value = string.Empty;
            return key.Length > 0;
        }

        key = string.Empty;
        value = string.Empty;
        return false;
    }

    /// <summary>Indexes and names from <c>/Get-WimInfo /WimFile:X</c> (no <c>/Index</c>).</summary>
    public static IReadOnlyList<WimIndexSummary> ParseWimIndexes(string output) =>
        [.. ParseRecords(output, IndexKey)
            .Select(r => new WimIndexSummary(
                ParseInt(r.Get(IndexKey)),
                r.Get("Name") ?? string.Empty,
                r.Get("Description") ?? string.Empty,
                ParseBytes(r.Get("Size"))))
            .Where(s => s.Index > 0)];

    /// <summary>
    /// The full edition record from <c>/Get-WimInfo /WimFile:X /Index:N</c>, which — unlike the
    /// index listing — carries architecture, version and edition id.
    /// </summary>
    public static ImageEdition? ParseWimImageDetail(string output)
    {
        var records = ParseRecords(output, IndexKey);
        if (records.Count == 0)
        {
            return null;
        }

        var record = records[0];

        var index = ParseInt(record.Get(IndexKey));
        if (index <= 0)
        {
            return null;
        }

        var name = record.Get("Name") ?? string.Empty;

        return new ImageEdition
        {
            Index = index,
            Name = name,
            Description = record.Get("Description") ?? name,
            EditionId = record.Get("Edition") ?? string.Empty,
            Architecture = record.Get("Architecture") ?? string.Empty,
            Version = ParseImageVersion(record),
            SizeBytes = ParseBytes(record.Get("Size")),
            DefaultLanguage = ParseDefaultLanguage(record),
        };
    }

    /// <summary>Mounted images from <c>/Get-MountedWimInfo</c>.</summary>
    /// <remarks>
    /// The spike noted that <c>Microsoft.Dism</c> offers <c>GetMountedImages()</c> natively, so this
    /// parser may look redundant. It is not: <see cref="DismExeBackend"/> is the backend that must
    /// work with no native dependency at all, and preflight crash recovery is exactly the moment
    /// you least want an extra moving part. See docs/spikes/dism-backend.md §2.
    /// </remarks>
    public static IReadOnlyList<MountedImage> ParseMountedImages(string output) =>
        [.. ParseRecords(output, MountDirKey)
            .Select(r => new
            {
                MountDir = r.Get(MountDirKey),
                ImageFile = r.Get("Image File"),
                Index = ParseInt(r.Get("Image Index")),
            })
            .Where(x => !string.IsNullOrEmpty(x.MountDir) && !string.IsNullOrEmpty(x.ImageFile))
            .Select(x => new MountedImage(x.ImageFile!, x.Index, x.MountDir!))];

    /// <summary>Provisioned packages from <c>/Get-ProvisionedAppxPackages</c>.</summary>
    public static IReadOnlyList<ProvisionedAppx> ParseProvisionedAppx(string output) =>
        [.. ParseRecords(output, PackageNameKey)
            .Select(r => new ProvisionedAppx(
                r.Get(PackageNameKey) ?? string.Empty,
                r.Get("DisplayName") ?? string.Empty,
                NullIfEmpty(r.Get("Version"))))
            .Where(p => p.PackageName.Length > 0)];

    /// <summary>Capability identity to state, from <c>/Get-Capabilities</c>.</summary>
    public static IReadOnlyDictionary<string, DismComponentState> ParseCapabilities(string output) =>
        ParseStates(output, CapabilityIdentityKey);

    /// <summary>Feature name to state, from <c>/Get-Features</c>.</summary>
    public static IReadOnlyDictionary<string, DismComponentState> ParseFeatures(string output) =>
        ParseStates(output, FeatureNameKey);

    /// <summary>Package identity to state, from <c>/Get-Packages</c>.</summary>
    public static IReadOnlyDictionary<string, DismComponentState> ParsePackages(string output) =>
        ParseStates(output, PackageIdentityKey);

    private static Dictionary<string, DismComponentState> ParseStates(string output, string identityKey)
    {
        var states = new Dictionary<string, DismComponentState>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in ParseRecords(output, identityKey))
        {
            var identity = record.Get(identityKey);
            if (string.IsNullOrEmpty(identity))
            {
                continue;
            }

            states[identity] = ParseState(record.Get(StateKey));
        }

        return states;
    }

    public static DismComponentState ParseState(string? state) => state?.Trim() switch
    {
        null or "" => DismComponentState.Other,
        "Installed" => DismComponentState.Installed,
        "Enabled" => DismComponentState.Enabled,
        "Disabled" => DismComponentState.Disabled,
        "Disabled with Payload Removed" => DismComponentState.DisabledWithPayloadRemoved,
        "Not Present" => DismComponentState.Absent,
        "Superseded" => DismComponentState.Superseded,
        "Staged" => DismComponentState.Staged,
        "Install Pending" => DismComponentState.Staged,
        _ => DismComponentState.Other,
    };

    /// <summary>
    /// Pulls the error code out of DISM's prose, as a backstop for the exit code.
    /// </summary>
    /// <remarks>
    /// The exit code is authoritative and is what <see cref="DismExeBackend"/> keys on. This exists
    /// because DISM's stdout sometimes carries a more specific HRESULT than the process exit code
    /// does, and that detail is worth putting in the exception message.
    /// </remarks>
    public static bool TryParseErrorCode(string output, out int code)
    {
        code = 0;
        if (string.IsNullOrEmpty(output))
        {
            return false;
        }

        var match = ErrorCodeRegex().Match(output);
        if (!match.Success)
        {
            return false;
        }

        var hex = match.Groups["hex"].Value;
        if (hex.Length > 0)
        {
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedHex))
            {
                return false;
            }

            code = unchecked((int)parsedHex);
            return true;
        }

        return int.TryParse(
            match.Groups["dec"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
    }

    /// <summary>True when DISM printed its success sentinel. Only meaningful under <c>/English</c>.</summary>
    public static bool ReportsSuccess(string output) =>
        output.Contains("The operation completed successfully", StringComparison.OrdinalIgnoreCase);

    private static Version ParseImageVersion(DismRecord record)
    {
        // DISM splits the build's revision into a separate field: "Version : 10.0.26200" plus
        // "ServicePack Build : 8037" is what the rest of the world writes as 26200.8037.
        if (!Version.TryParse(record.Get("Version"), out var version))
        {
            return new Version(0, 0);
        }

        var revision = ParseInt(record.Get("ServicePack Build"));
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(revision, 0));
    }

    private static string? ParseDefaultLanguage(DismRecord record)
    {
        foreach (var entry in record.GetAll("Languages"))
        {
            if (entry.Length == 0)
            {
                continue;
            }

            var marker = entry.IndexOf(" (Default)", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                return entry[..marker].Trim();
            }
        }

        return NullIfEmpty(record.Get("Default Language"));
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    /// <summary>Parses "23,831,001,491 bytes". Separators are stripped rather than trusted to a culture.</summary>
    private static long ParseBytes(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var digits = new string([.. value.TakeWhile(c => char.IsDigit(c) || c is ',' or '.' or ' ')
            .Where(char.IsDigit)]);

        return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"Error:\s*(?:0x(?<hex>[0-9a-fA-F]{1,8})|(?<dec>\d{1,10}))", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRegex();
}

/// <summary>One row of <c>/Get-WimInfo</c>'s index listing, before the per-index detail call.</summary>
public sealed record WimIndexSummary(int Index, string Name, string Description, long SizeBytes);
