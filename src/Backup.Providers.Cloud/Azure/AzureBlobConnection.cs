using Azure.Storage.Blobs;
using Backup.Application.Providers;

namespace Backup.Providers.Cloud.Azure;

internal static class AzureBlobConnection
{
    public static IReadOnlyList<SettingField> CommonFields =>
    [
        SettingField.Secret("connectionString", "Cadena de conexión", required: true,
            help: "Portal de Azure → Cuenta de almacenamiento → Claves de acceso. También acepta una URL SAS del contenedor."),
        SettingField.Text("container", "Contenedor", required: true, help: "Se ignora si la cadena es una URL SAS de contenedor."),
        SettingField.Text("prefix", "Prefijo / carpeta", placeholder: "backups/produccion").Browsable(),
    ];

    public static BlobContainerClient CreateContainer(ProviderSettings settings)
    {
        var connection = settings.Require("connectionString");
        return connection.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new BlobContainerClient(new Uri(connection))
            : new BlobContainerClient(connection, settings.Require("container"));
    }

    public static string BlobName(ProviderSettings settings, string relative) =>
        ProviderHelpers.CombineRemote(settings.Get("prefix"), relative);

    /// <summary>Lista un "nivel" del contenedor usando '/' como delimitador. Rutas sin '/' inicial.</summary>
    public static async Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken)
    {
        var container = CreateContainer(settings);
        var current = (path ?? string.Empty).Trim('/');
        var items = new List<FolderItem>();
        await foreach (var item in container.GetBlobsByHierarchyAsync(
            new global::Azure.Storage.Blobs.Models.GetBlobsByHierarchyOptions { Delimiter = "/", Prefix = current.Length == 0 ? null : current + "/" },
            cancellationToken))
        {
            if (item.IsPrefix)
            {
                var prefix = item.Prefix.TrimEnd('/');
                items.Add(new FolderItem(prefix[(prefix.LastIndexOf('/') + 1)..], prefix, true));
            }
            else
            {
                var name = item.Blob.Name;
                items.Add(new FolderItem(name[(name.LastIndexOf('/') + 1)..], name, false, item.Blob.Properties.ContentLength, item.Blob.Properties.LastModified));
            }

            if (items.Count >= 5000)
            {
                break;
            }
        }

        return FolderListing.Create(current, FolderListing.SlashParent(current, rooted: false), items);
    }

    public static async Task<string> TestAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var container = CreateContainer(settings);
        var exists = await container.ExistsAsync(cancellationToken);
        return exists.Value
            ? $"Acceso correcto al contenedor '{container.Name}'."
            : $"Conexión correcta, pero el contenedor '{container.Name}' no existe todavía.";
    }
}
