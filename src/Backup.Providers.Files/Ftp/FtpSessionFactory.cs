using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;
using FluentFTP;

namespace Backup.Providers.Files.Ftp;

/// <summary>FTP / FTPS con FluentFTP.</summary>
internal sealed class FtpSessionFactory : IFileSessionFactory
{
    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "ftp",
        "FTP / FTPS",
        role == ProviderRole.Source ? "Descarga una carpeta FTP y la empaqueta en .zip." : "Servidor FTP con cifrado TLS opcional.",
        ProviderCategory.FileTransfer,
        "server",
        [
            SettingField.Text("host", "Host", required: true),
            SettingField.Number("port", "Puerto", 21),
            SettingField.Text("user", "Usuario", required: true),
            SettingField.Secret("password", "Contraseña"),
            SettingField.Text("remotePath", "Carpeta remota", placeholder: "/backups").Browsable(),
            SettingField.Select("encryption", "Cifrado", ["Explicit", "Implicit", "None"], "Explicit",
                "Explicit = FTPS (AUTH TLS). None = FTP plano (no recomendado)."),
            SettingField.Toggle("acceptAnyCertificate", "Aceptar cualquier certificado", false, "Útil con certificados autofirmados."),
            .. role == ProviderRole.Source ? FileSessionSource.FilterFields("remotePath") : [],
        ]);

    public string RootPath(ProviderSettings settings) => (settings.Get("remotePath") ?? string.Empty).TrimEnd('/');

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        FileSessionBrowsing.BrowseAsync(this, settings, path, cancellationToken);

    public async Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var client = new AsyncFtpClient(
            settings.Require("host"),
            settings.Require("user"),
            settings.GetRaw("password") ?? string.Empty,
            settings.GetInt("port", 21));

        client.Config.EncryptionMode = settings.Get("encryption", "Explicit") switch
        {
            "Implicit" => FtpEncryptionMode.Implicit,
            "None" => FtpEncryptionMode.None,
            _ => FtpEncryptionMode.Explicit,
        };
        client.Config.ValidateAnyCertificate = settings.GetBool("acceptAnyCertificate");
        client.Config.ConnectTimeout = 15_000;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

        try
        {
            await client.Connect(cancellationToken);
            var home = await client.GetWorkingDirectory(cancellationToken);
            return new FtpSession(client, string.IsNullOrWhiteSpace(home) ? "/" : home);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <param name="home">Carpeta inicial del usuario; las rutas relativas se resuelven desde ella.</param>
    private sealed class FtpSession(AsyncFtpClient client, string home) : IFileSession
    {
        public string HomeDirectory => home;

        public async Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken)
        {
            var status = await client.UploadFile(localFile, Absolute(remotePath), FtpRemoteExists.Overwrite, createRemoteDir: true, token: cancellationToken);
            if (status == FtpStatus.Failed)
            {
                throw new IOException($"No se pudo subir {remotePath}.");
            }
        }

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var path = Absolute(remoteDirectory);
            if (!await client.DirectoryExists(path, cancellationToken))
            {
                return [];
            }

            var items = await client.GetListing(path, cancellationToken);
            if (!items.Any(IsEntry))
            {
                // Algunos servidores (p. ej. ProFTPD en NAS) devuelven MLSD vacío en ciertas carpetas; LIST sí las muestra.
                items = await client.GetListing(path, FtpListOption.ForceList, cancellationToken);
            }

            return [.. items
                .Where(IsEntry)
                .Select(i => new RemoteEntry(
                    ProviderHelpers.CombineRemote(remoteDirectory, i.Name),
                    i.Name,
                    i.Type == FtpObjectType.Directory,
                    i.Modified == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(i.Modified, DateTimeKind.Utc)),
                    i.Type == FtpObjectType.File ? i.Size : null))];
        }

        public async Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken)
        {
            var status = await client.DownloadFile(localFile, Absolute(remotePath), FtpLocalExists.Overwrite, token: cancellationToken);
            if (status == FtpStatus.Failed)
            {
                throw new IOException($"No se pudo descargar {remotePath}.");
            }
        }

        public Task DeleteAsync(string remotePath, CancellationToken cancellationToken) =>
            client.DeleteFile(Absolute(remotePath), cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await client.Disconnect();
            }
            finally
            {
                client.Dispose();
            }
        }

        private static bool IsEntry(FtpListItem item) => item.Type is FtpObjectType.File or FtpObjectType.Directory;

        /// <summary>Rutas con '/' inicial son absolutas; el resto se resuelve desde la carpeta inicial.</summary>
        private string Absolute(string path) => RemotePaths.Resolve(home, path);
    }
}

public sealed class FtpSource() : FileSessionSource(new FtpSessionFactory());

public sealed class FtpDestination() : FileSessionDestination(new FtpSessionFactory());
