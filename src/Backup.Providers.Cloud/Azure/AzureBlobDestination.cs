using Azure.Storage.Blobs.Models;
using Backup.Application.Providers;

namespace Backup.Providers.Cloud.Azure;

public sealed class AzureBlobDestination : IBackupDestination, IConnectionTester, IFolderBrowser
{
    public ProviderDescriptor Descriptor { get; } = new(
        "azureblob",
        "Azure Blob Storage",
        "Contenedor de Azure Storage con nivel de acceso configurable.",
        ProviderCategory.Cloud,
        "cloud",
        [
            .. AzureBlobConnection.CommonFields,
            SettingField.Select("accessTier", "Nivel de acceso", ["Hot", "Cool", "Cold", "Archive"], "Cool"),
            SettingField.Toggle("createContainer", "Crear el contenedor si no existe", true),
        ]);

    public async Task UploadAsync(DestinationContext context, string localFile, string objectName, CancellationToken cancellationToken)
    {
        var container = AzureBlobConnection.CreateContainer(context.Settings);
        if (context.Settings.GetBool("createContainer", true))
        {
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        var blob = container.GetBlobClient(AzureBlobConnection.BlobName(context.Settings, objectName));
        await blob.UploadAsync(localFile, new BlobUploadOptions
        {
            AccessTier = new AccessTier(context.Settings.Get("accessTier", "Cool")),
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredBackup>> ListAsync(DestinationContext context, string folder, CancellationToken cancellationToken)
    {
        var container = AzureBlobConnection.CreateContainer(context.Settings);
        var root = context.Settings.Get("prefix");
        var prefix = AzureBlobConnection.BlobName(context.Settings, folder).TrimEnd('/') + "/";
        var result = new List<StoredBackup>();

        if (!(await container.ExistsAsync(cancellationToken)).Value)
        {
            return result;
        }

        await foreach (var blob in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken))
        {
            result.Add(new StoredBackup(ProviderHelpers.MakeRelative(root, blob.Name), blob.Properties.LastModified, blob.Properties.ContentLength));
        }

        return result;
    }

    public async Task DeleteAsync(DestinationContext context, string objectName, CancellationToken cancellationToken)
    {
        var container = AzureBlobConnection.CreateContainer(context.Settings);
        await container.DeleteBlobIfExistsAsync(AzureBlobConnection.BlobName(context.Settings, objectName), cancellationToken: cancellationToken);
    }

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        AzureBlobConnection.BrowseAsync(settings, path, cancellationToken);

    public Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        AzureBlobConnection.TestAsync(settings, cancellationToken);
}
