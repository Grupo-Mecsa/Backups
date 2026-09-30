using Azure.Storage.Blobs.Models;
using Backup.Application.Providers;

namespace Backup.Providers.Cloud.Azure;

/// <summary>Descarga los blobs bajo un prefijo y los empaqueta en un .zip.</summary>
public sealed class AzureBlobSource : IBackupSource, IConnectionTester, IFolderBrowser
{
    public ProviderDescriptor Descriptor { get; } = new(
        "azureblob",
        "Azure Blob Storage",
        "Respalda el contenido de un contenedor o prefijo en un .zip.",
        ProviderCategory.Cloud,
        "cloud",
        [
            .. AzureBlobConnection.CommonFields,
            SettingField.Selection("selection", "prefix"),
            SettingField.Text("include", "Incluir por patrón", placeholder: "*.pdf, *.xlsx", help: "Patrones separados por coma. Vacío = todo."),
        ]);

    public string? ArtifactExtension(ProviderSettings settings) => ".zip";

    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var container = AzureBlobConnection.CreateContainer(settings);
        var root = settings.Get("prefix");
        var prefix = string.IsNullOrEmpty(root) ? null : root.TrimEnd('/') + "/";
        var include = ProviderHelpers.GlobFilter(settings.GetList("include"));
        var selection = PathSelection.Parse(settings.GetRaw("selection"));
        var staging = Directory.CreateDirectory(Path.Combine(context.WorkingDirectory, "blobs")).FullName;

        var count = 0;
        await foreach (var blob in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken))
        {
            if (!include(Path.GetFileName(blob.Name)) || !selection.IsIncluded(PathSelection.Relative(root, blob.Name)))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(staging, ProviderHelpers.MakeRelative(root, blob.Name)));
            if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await container.GetBlobClient(blob.Name).DownloadToAsync(target, cancellationToken);
            count++;
        }

        context.Log.Info($"{count} blobs descargados de {container.Name}/{prefix}");
        return await ProviderHelpers.ZipDirectoryAsync(staging, context, cancellationToken);
    }

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        AzureBlobConnection.BrowseAsync(settings, path, cancellationToken);

    public Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        AzureBlobConnection.TestAsync(settings, cancellationToken);
}
