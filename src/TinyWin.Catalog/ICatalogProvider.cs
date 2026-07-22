namespace TinyWin.Catalog;

/// <summary>Supplies the component catalog. Implementations may embed, read from disk, or fake it.</summary>
public interface ICatalogProvider
{
    Task<CatalogDocument> LoadAsync(CancellationToken cancellationToken = default);
}
