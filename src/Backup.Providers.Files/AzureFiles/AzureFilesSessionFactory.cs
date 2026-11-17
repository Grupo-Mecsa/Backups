using Azure;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;

namespace Backup.Providers.Files.AzureFiles;

/// <summary>
/// Recurso compartido de Azure Files por su API REST (HTTPS 443): a diferencia de montarlo por SMB, no necesita el
/// puerto 445, que muchos proveedores de internet bloquean.
/// </summary>
internal sealed class AzureFilesSessionFactory : IFileSessionFactory
{
    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "azurefiles",
        "Azure Files",
        role == ProviderRole.Source
            ? "Descarga una carpeta de un recurso compartido de Azure Files y la empaqueta en .zip."
            : "Recurso compartido de Azure Files (por HTTPS, sin el puerto 445).",
        ProviderCategory.Cloud,
        "cloud",
        [
            SettingField.Secret("connectionString", "Cadena de conexión", required: true,
                help: "Portal de Azure → Cuenta de almacenamiento → Claves de acceso. También acepta una URL SAS del recurso compartido.").ForConnection(),
            SettingField.Text("share", "Recurso compartido", required: true, help: "Se ignora si la cadena es una URL SAS del recurso.").ForConnection(),
            SettingField.Text("remotePath", "Carpeta", placeholder: "/respaldos").Browsable(),
            .. role == ProviderRole.Source ? FileSessionSource.FilterFields("remotePath") : [],
        ]);

    public string RootPath(ProviderSettings settings) => RemotePaths.Root(settings.Get("remotePath"));

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        FileSessionBrowsing.BrowseAsync(this, settings, path, cancellationToken);

    public async Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var connection = settings.Require("connectionString");
        var share = connection.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new ShareClient(new Uri(connection))
            : new ShareClient(connection, settings.Require("share"));
        if (!(await share.ExistsAsync(cancellationToken)).Value)
        {
            throw new InvalidOperationException($"El recurso compartido '{share.Name}' no existe en la cuenta {share.AccountName}.");
        }

        return new AzureFilesSession(share);
    }

    private sealed class AzureFilesSession(ShareClient share) : IFileSession
    {
        private readonly HashSet<string> _knownFolders = new(StringComparer.Ordinal);

        private ShareDirectoryClient Directory(string path) =>
            CloudHttp.Trim(path) is { Length: > 0 } clean ? share.GetDirectoryClient(clean) : share.GetRootDirectoryClient();

        private ShareFileClient File(string path)
        {
            var (directory, name) = CloudHttp.Split(path);
            return Directory(directory).GetFileClient(name);
        }

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var entries = new List<RemoteEntry>();
            try
            {
                await foreach (var item in Directory(remoteDirectory).GetFilesAndDirectoriesAsync(
                    new ShareDirectoryGetFilesAndDirectoriesOptions { Traits = ShareFileTraits.Timestamps, IncludeExtendedInfo = true },
                    cancellationToken))
                {
                    entries.Add(new RemoteEntry(
                        RemotePaths.Combine(remoteDirectory, item.Name),
                        item.Name,
                        item.IsDirectory,
                        item.Properties?.LastWrittenOn ?? item.Properties?.LastModified,
                        item.IsDirectory ? null : item.FileSize));
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return [];
            }

            return entries;
        }

        public async Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken)
        {
            try
            {
                using var download = (await File(remotePath).DownloadAsync(cancellationToken: cancellationToken)).Value;
                await using var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await download.Content.CopyToAsync(output, cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                throw new FileNotFoundException($"No existe en Azure Files: {remotePath}", ex);
            }
        }

        public async Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken)
        {
            var (directory, _) = CloudHttp.Split(remotePath);
            var current = string.Empty;
            foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? part : current + "/" + part;
                if (_knownFolders.Add(current))
                {
                    await share.GetDirectoryClient(current).CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                }
            }

            var file = File(remotePath);
            await using var input = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await file.CreateAsync(input.Length, cancellationToken: cancellationToken);
            await file.UploadAsync(input, cancellationToken: cancellationToken);
        }

        public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken) =>
            await File(remotePath).DeleteIfExistsAsync(cancellationToken: cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class AzureFilesSource() : FileSessionSource(new AzureFilesSessionFactory());

public sealed class AzureFilesDestination() : FileSessionDestination(new AzureFilesSessionFactory());
