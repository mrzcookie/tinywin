using TinyWin.Registry.Interop;
using Win32ValueKind = Microsoft.Win32.RegistryValueKind;

namespace TinyWin.Registry.Tests.Fakes;

/// <summary>
/// An in-memory stand-in for Win32, so everything above the P/Invoke seam can be tested without an
/// elevated process or a mounted image.
/// </summary>
/// <remarks>
/// It models the two behaviours that actually decide correctness: keys either exist or they do not
/// (which drives no-target reporting), and unload can be made to fail a chosen number of times
/// (which drives the retry loop). Everything else is a dictionary.
/// </remarks>
internal sealed class FakeNativeRegistry : INativeRegistry
{
    private readonly Dictionary<string, Dictionary<string, (object Data, Win32ValueKind Kind)>> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every call made, in order, as "verb:argument" — lets tests assert on sequencing.</summary>
    public List<string> Calls { get; } = [];

    public int PrivilegeEnableCount { get; private set; }

    /// <summary>Mount points currently loaded, in load order.</summary>
    public List<string> Loaded { get; } = [];

    /// <summary>Mount names that were unloaded, in the order they were unloaded.</summary>
    public List<string> UnloadOrder { get; } = [];

    /// <summary>Mount names that <see cref="LoadHive"/> should refuse, with the reason.</summary>
    public Dictionary<string, string> LoadFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many times <see cref="UnloadHive"/> should fail before succeeding. <see cref="int.MaxValue"/>
    /// models a hive that is genuinely stuck.
    /// </summary>
    public Dictionary<string, int> UnloadFailuresRemaining { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra <c>HKLM</c> subkey names to report, for stranded-hive recovery tests.</summary>
    public List<string> ExtraHklmSubkeys { get; } = [];

    public int UnloadAttempts { get; private set; }

    /// <summary>Runs just before each load succeeds — lets a test cancel partway through.</summary>
    public Action<string>? OnLoad { get; set; }

    public void EnableHivePrivileges()
    {
        PrivilegeEnableCount++;
        Calls.Add("privileges");
    }

    public void LoadHive(string mountName, string hiveFilePath)
    {
        Calls.Add($"load:{mountName}:{hiveFilePath}");
        OnLoad?.Invoke(mountName);

        if (LoadFailures.TryGetValue(mountName, out var reason))
        {
            throw new RegistryOperationException(reason, 5);
        }

        Loaded.Add(mountName);
        _keys[mountName] = [];
    }

    public void UnloadHive(string mountName)
    {
        UnloadAttempts++;
        Calls.Add($"unload:{mountName}");

        if (UnloadFailuresRemaining.TryGetValue(mountName, out var remaining) && remaining > 0)
        {
            if (remaining != int.MaxValue)
            {
                UnloadFailuresRemaining[mountName] = remaining - 1;
            }

            throw new RegistryOperationException($"Could not unload HKLM\\{mountName}", 5);
        }

        if (!Loaded.Remove(mountName) && !ExtraHklmSubkeys.Remove(mountName))
        {
            throw new RegistryOperationException($"No hive loaded at HKLM\\{mountName}", 2);
        }

        UnloadOrder.Add(mountName);
        RemoveSubtree(mountName);
    }

    public IReadOnlyList<string> GetLoadedHiveNames() =>
        [.. new[] { "SOFTWARE", "SYSTEM" }.Concat(Loaded).Concat(ExtraHklmSubkeys)];

    public bool KeyExists(string keyPath) => _keys.ContainsKey(keyPath);

    public string? GetStringValue(string keyPath, string valueName) =>
        _keys.TryGetValue(keyPath, out var values) && values.TryGetValue(valueName, out var value)
            ? value.Data as string
            : null;

    public void CreateKey(string keyPath)
    {
        Calls.Add($"create:{keyPath}");

        // Mirrors RegCreateKeyEx: ancestors are materialised too.
        var segments = keyPath.Split('\\');
        for (var i = 1; i <= segments.Length; i++)
        {
            var path = string.Join('\\', segments[..i]);
            if (!_keys.ContainsKey(path))
            {
                _keys[path] = [];
            }
        }
    }

    public void SetValue(string keyPath, string valueName, object data, Win32ValueKind kind)
    {
        Calls.Add($"set:{keyPath}:{valueName}");

        if (!_keys.TryGetValue(keyPath, out var values))
        {
            throw new RegistryOperationException($"Key HKLM\\{keyPath} does not exist", 2);
        }

        values[valueName] = (data, kind);
    }

    public bool DeleteKeyTree(string keyPath)
    {
        Calls.Add($"deleteKey:{keyPath}");

        if (!_keys.ContainsKey(keyPath))
        {
            return false;
        }

        RemoveSubtree(keyPath);
        return true;
    }

    public bool DeleteValue(string keyPath, string valueName)
    {
        Calls.Add($"deleteValue:{keyPath}:{valueName}");
        return _keys.TryGetValue(keyPath, out var values) && values.Remove(valueName);
    }

    /// <summary>Test helper: reads back what a set actually wrote.</summary>
    public (object Data, Win32ValueKind Kind)? Read(string keyPath, string valueName) =>
        _keys.TryGetValue(keyPath, out var values) && values.TryGetValue(valueName, out var value) ? value : null;

    /// <summary>Test helper: seeds a key so a delete has something to find.</summary>
    public void Seed(string keyPath, string? valueName = null, object? data = null)
    {
        CreateKey(keyPath);
        Calls.Clear();

        if (valueName is not null)
        {
            _keys[keyPath][valueName] = (data ?? 1, Win32ValueKind.DWord);
        }
    }

    /// <summary>
    /// Test helper: seeds a scheduled task registration the way a real SOFTWARE hive carries one —
    /// a Tree node holding the GUID, plus entries under the id-keyed subkeys named.
    /// </summary>
    public void SeedTask(string mountName, string taskName, string id, params string[] idKeyedSubkeys)
    {
        var tree = $@"{mountName}\{TaskCache.TreePath(taskName)}";
        CreateKey(tree);
        _keys[tree][TaskCache.IdValueName] = (id, Win32ValueKind.String);

        foreach (var subkey in idKeyedSubkeys)
        {
            CreateKey($@"{mountName}\{TaskCache.IdKeyedPath(subkey, id)}");
        }

        Calls.Clear();
    }

    private void RemoveSubtree(string keyPath)
    {
        var prefix = keyPath + "\\";
        foreach (var key in _keys.Keys
            .Where(k => k.Equals(keyPath, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            _keys.Remove(key);
        }
    }
}
