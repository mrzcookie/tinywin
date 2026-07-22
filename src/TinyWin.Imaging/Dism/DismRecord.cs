namespace TinyWin.Imaging.Dism;

/// <summary>
/// One <c>Key : Value</c> block of DISM output.
/// </summary>
/// <remarks>
/// Every DISM listing command — <c>/Get-WimInfo</c>, <c>/Get-ProvisionedAppxPackages</c>,
/// <c>/Get-Capabilities</c>, <c>/Get-Features</c>, <c>/Get-Packages</c>,
/// <c>/Get-MountedWimInfo</c> — emits the same shape: blocks of <c>Key : Value</c> lines separated
/// by blank lines. Parsing that shape once and typing it afterwards is what keeps
/// <see cref="DismOutputParser"/> small.
/// </remarks>
public sealed class DismRecord
{
    private readonly Dictionary<string, List<string>> _fields;

    internal DismRecord(Dictionary<string, List<string>> fields) => _fields = fields;

    public bool Has(string key) => _fields.ContainsKey(key);

    /// <summary>The field's value, or null when absent. Empty values come back as empty strings.</summary>
    public string? Get(string key) => _fields.TryGetValue(key, out var values) ? values[0] : null;

    /// <summary>
    /// The field's value plus any indented continuation lines beneath it — how DISM renders
    /// <c>Languages :</c>.
    /// </summary>
    public IReadOnlyList<string> GetAll(string key) =>
        _fields.TryGetValue(key, out var values) ? values : [];

    public IEnumerable<string> Keys => _fields.Keys;
}
