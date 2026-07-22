using System.Reflection;
using System.Text.Json;

namespace TinyWin.Catalog;

/// <summary>
/// Loads the catalog from JSON embedded in this assembly, so the portable single-file exe carries
/// its own data with no side files.
/// </summary>
public sealed class EmbeddedCatalogProvider : ICatalogProvider
{
    private readonly Assembly _assembly;

    public EmbeddedCatalogProvider(Assembly? assembly = null) =>
        _assembly = assembly ?? typeof(EmbeddedCatalogProvider).Assembly;

    public async Task<CatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        var names = _assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var parts = new List<CatalogDocument>(names.Count);
        foreach (var name in names)
        {
            await using var stream = _assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            var part = await JsonSerializer.DeserializeAsync<CatalogDocument>(
                stream, CatalogJson.Options, cancellationToken).ConfigureAwait(false);

            if (part is not null)
            {
                parts.Add(part);
            }
        }

        return CatalogDocument.Merge(parts);
    }
}

/// <summary>Loads the catalog from a directory of JSON files. Used by tests and by --catalog-dir.</summary>
public sealed class DirectoryCatalogProvider : ICatalogProvider
{
    private readonly string _directory;

    public DirectoryCatalogProvider(string directory) => _directory = directory;

    public async Task<CatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        var parts = new List<CatalogDocument>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(file);
            try
            {
                var part = await JsonSerializer.DeserializeAsync<CatalogDocument>(
                    stream, CatalogJson.Options, cancellationToken).ConfigureAwait(false);

                if (part is not null)
                {
                    parts.Add(part);
                }
            }
            catch (JsonException ex)
            {
                // Name the file. A bare "invalid JSON" against 40 catalog files is useless.
                throw new InvalidDataException($"Catalog file '{file}' is not valid: {ex.Message}", ex);
            }
        }

        return CatalogDocument.Merge(parts);
    }
}
