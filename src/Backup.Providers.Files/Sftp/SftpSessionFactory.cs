using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;
using Renci.SshNet;

namespace Backup.Providers.Files.Sftp;

/// <summary>SFTP (SSH) con SSH.NET: autenticación por contraseña o llave privada.</summary>
internal sealed class SftpSessionFactory : IFileSessionFactory
{
    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "sftp",
        "SFTP",
        role == ProviderRole.Source ? "Descarga una carpeta vía SFTP y la empaqueta en .zip." : "Servidor SSH/SFTP.",
        ProviderCategory.FileTransfer,
        "server",
        [
            SettingField.Text("host", "Host", required: true).ForConnection(),
            SettingField.Number("port", "Puerto", 22).ForConnection(),
            SettingField.Text("user", "Usuario", required: true).ForConnection(),
            SettingField.Secret("password", "Contraseña", help: "Vacío si usas llave privada.").ForConnection(),
            SettingField.Text("privateKeyPath", "Ruta de la llave privada", placeholder: "/keys/id_ed25519", help: "Archivo montado en el contenedor.").ForConnection(),
            SettingField.Secret("privateKeyPassphrase", "Passphrase de la llave").ForConnection(),
            SettingField.Text("remotePath", "Carpeta remota", placeholder: "/home/backup").Browsable(),
            SettingField.Text("hostKeyFingerprint", "Huella SHA256 del host (opcional)", placeholder: "AbCdEf...",
                help: "Si se indica, la conexión se rechaza si el servidor no coincide.").ForConnection(),
            .. role == ProviderRole.Source ? FileSessionSource.FilterFields("remotePath") : [],
        ]);

    public string RootPath(ProviderSettings settings) => RemotePaths.Root(settings.Get("remotePath"));

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        FileSessionBrowsing.BrowseAsync(this, settings, path, cancellationToken);

    public async Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var user = settings.Require("user");
        var methods = new List<AuthenticationMethod>();
        if (settings.Get("privateKeyPath") is { } keyPath)
        {
            var keyFile = settings.GetRaw("privateKeyPassphrase") is { } passphrase
                ? new PrivateKeyFile(keyPath, passphrase)
                : new PrivateKeyFile(keyPath);
            methods.Add(new PrivateKeyAuthenticationMethod(user, keyFile));
        }

        if (settings.GetRaw("password") is { } password)
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
        }

        if (methods.Count == 0)
        {
            throw new InvalidOperationException("Indica una contraseña o una llave privada.");
        }

        var connection = new ConnectionInfo(settings.Require("host"), settings.GetInt("port", 22), user, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        var client = new SftpClient(connection);
        if (settings.Get("hostKeyFingerprint") is { } expected)
        {
            var normalized = expected.Replace("SHA256:", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('=');
            client.HostKeyReceived += (_, e) => e.CanTrust = string.Equals(e.FingerPrintSHA256.TrimEnd('='), normalized, StringComparison.Ordinal);
        }

        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new SftpSession(client, string.IsNullOrWhiteSpace(client.WorkingDirectory) ? "/" : client.WorkingDirectory);
    }

    /// <param name="home">Carpeta inicial del usuario; las rutas relativas se resuelven desde ella.</param>
    private sealed class SftpSession(SftpClient client, string home) : IFileSession
    {
        public string HomeDirectory => home;

        public Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken) => Task.Run(() =>
        {
            var path = Absolute(remotePath);
            EnsureDirectory(path[..path.LastIndexOf('/')]);
            using var input = File.OpenRead(localFile);
            client.UploadFile(input, path, canOverride: true);
        }, cancellationToken);

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<RemoteEntry>>(() =>
        {
            var path = Absolute(remoteDirectory);
            if (!client.Exists(path))
            {
                return [];
            }

            return [.. client.ListDirectory(path)
                .Where(f => f.Name is not "." and not ".." && (f.IsDirectory || f.IsRegularFile))
                .Select(f => new RemoteEntry(
                    RemotePaths.Combine(remoteDirectory, f.Name),
                    f.Name,
                    f.IsDirectory,
                    new DateTimeOffset(DateTime.SpecifyKind(f.LastWriteTimeUtc, DateTimeKind.Utc)),
                    f.IsRegularFile ? f.Length : null))];
        }, cancellationToken);

        public Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken) => Task.Run(() =>
        {
            using var output = File.Create(localFile);
            client.DownloadFile(Absolute(remotePath), output);
        }, cancellationToken);

        public Task DeleteAsync(string remotePath, CancellationToken cancellationToken) =>
            Task.Run(() => client.DeleteFile(Absolute(remotePath)), cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }

            client.Dispose();
            return ValueTask.CompletedTask;
        }

        private void EnsureDirectory(string path)
        {
            var current = string.Empty;
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + part;
                if (!client.Exists(current))
                {
                    client.CreateDirectory(current);
                }
            }
        }

        private string Absolute(string path) => RemotePaths.Resolve(home, path);
    }
}

public sealed class SftpSource() : FileSessionSource(new SftpSessionFactory());

public sealed class SftpDestination() : FileSessionDestination(new SftpSessionFactory());
