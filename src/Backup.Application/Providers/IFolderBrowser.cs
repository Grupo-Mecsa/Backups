namespace Backup.Application.Providers;

/// <summary>Qué se puede elegir con el explorador en un campo de configuración.</summary>
public enum BrowseMode
{
    None,
    Folder,
    FolderOrFile,
}

/// <summary>Capacidad opcional (ISP): proveedores que permiten explorar sus carpetas desde la UI.</summary>
public interface IFolderBrowser
{
    /// <param name="path">Ruta a listar en el formato del proveedor. Null o vacío = raíz.</param>
    Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken);
}

/// <param name="Path">Ruta listada, tal como se guardaría en el campo.</param>
/// <param name="Parent">Ruta de la carpeta superior; null en la raíz.</param>
public sealed record FolderListing(string Path, string? Parent, IReadOnlyList<FolderItem> Items)
{
    public static FolderListing Create(string path, string? parent, IEnumerable<FolderItem> items) =>
        new(path, parent, [.. items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)]);

    /// <summary>Carpeta superior de una ruta con '/' como separador ("/a/b" → "/a", "/a" → "/", "/" → null).</summary>
    public static string? SlashParent(string path, bool rooted = true)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0)
        {
            return null;
        }

        var index = trimmed.LastIndexOf('/');
        var parent = index < 0 ? string.Empty : trimmed[..index];
        return rooted ? "/" + parent : parent;
    }
}

public sealed record FolderItem(string Name, string Path, bool IsDirectory, long? Size = null, DateTimeOffset? Modified = null);
