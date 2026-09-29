using System.Net;
using System.Net.Sockets;
using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;
using SMBLibrary;
using SMBLibrary.Client;
using SmbFileAttributes = SMBLibrary.FileAttributes;

namespace Backup.Providers.Files.Smb;

/// <summary>Carpetas compartidas de Windows / Samba (SMB2/3) sin necesidad de montar el recurso.</summary>
internal sealed class SmbSessionFactory : IFileSessionFactory
{
    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "smb",
        "SMB / Carpeta compartida",
        role == ProviderRole.Source ? "Descarga una carpeta compartida de Windows/Samba y la empaqueta en .zip." : "Carpeta compartida de Windows, NAS o Samba.",
        ProviderCategory.FileTransfer,
        "share",
        [
            SettingField.Text("host", "Servidor", required: true, placeholder: "nas01 o 192.168.1.10"),
            SettingField.Text("share", "Recurso compartido", required: true, placeholder: "Backups"),
            SettingField.Text("domain", "Dominio", placeholder: "EMPRESA"),
            SettingField.Text("user", "Usuario", required: true),
            SettingField.Secret("password", "Contraseña"),
            SettingField.Text("remotePath", "Carpeta dentro del recurso", placeholder: "sql/produccion").Browsable(),
            .. role == ProviderRole.Source ? FileSessionSource.FilterFields("remotePath") : [],
        ]);

    public string RootPath(ProviderSettings settings) => (settings.Get("remotePath") ?? string.Empty).Replace('\\', '/').Trim('/');

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        FileSessionBrowsing.BrowseAsync(this, settings, path, cancellationToken);

    public async Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var host = settings.Require("host");
        var address = IPAddress.TryParse(host, out var ip)
            ? ip
            : (await Dns.GetHostAddressesAsync(host, cancellationToken))
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .First();

        var client = new SMB2Client();
        if (!client.Connect(address, SMBTransportType.DirectTCPTransport))
        {
            throw new IOException($"No se pudo conectar a {host}:445.");
        }

        var status = client.Login(settings.Get("domain") ?? string.Empty, settings.Require("user"), settings.GetRaw("password") ?? string.Empty);
        Ensure(status, "Inicio de sesión SMB rechazado", client);

        var store = client.TreeConnect(settings.Require("share"), out status);
        Ensure(status, $"No se pudo abrir el recurso '{settings.Get("share")}'", client);

        return new SmbSession(client, store);
    }

    private static void Ensure(NTStatus status, string message, SMB2Client client)
    {
        if (status != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new IOException($"{message}: {SmbErrors.Describe(status)}");
        }
    }

    private sealed class SmbSession(SMB2Client client, ISMBFileStore store) : IFileSession
    {
        public Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken) => Task.Run(() =>
        {
            var path = ToSmb(remotePath);
            var separator = path.LastIndexOf('\\');
            if (separator > 0)
            {
                EnsureDirectory(path[..separator]);
            }

            var status = store.CreateFile(out var handle, out _, path,
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SmbFileAttributes.Normal, ShareAccess.None,
                CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            Check(status, $"crear {remotePath}");

            try
            {
                using var input = File.OpenRead(localFile);
                var buffer = new byte[(int)Math.Min(client.MaxWriteSize, 1 << 20)];
                long offset = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = read == buffer.Length ? buffer : buffer[..read];
                    Check(store.WriteFile(out _, handle, offset, chunk), $"escribir {remotePath}");
                    offset += read;
                }
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken);

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<RemoteEntry>>(() =>
        {
            var status = store.CreateFile(out var handle, out _, ToSmb(remoteDirectory),
                AccessMask.GENERIC_READ, SmbFileAttributes.Directory, ShareAccess.Read | ShareAccess.Write,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
            {
                return [];
            }

            Check(status, $"abrir {(string.IsNullOrWhiteSpace(remoteDirectory.Trim('/')) ? "la raíz del recurso" : remoteDirectory)}");
            try
            {
                store.QueryDirectory(out var entries, handle, "*", FileInformationClass.FileDirectoryInformation);
                return [.. (entries ?? [])
                    .OfType<FileDirectoryInformation>()
                    .Where(e => e.FileName is not "." and not "..")
                    .Select(e =>
                    {
                        var isDirectory = e.FileAttributes.HasFlag(SmbFileAttributes.Directory);
                        return new RemoteEntry(
                            ProviderHelpers.CombineRemote(remoteDirectory, e.FileName),
                            e.FileName,
                            isDirectory,
                            new DateTimeOffset(DateTime.SpecifyKind(e.LastWriteTime, DateTimeKind.Utc)),
                            isDirectory ? null : e.EndOfFile);
                    })];
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken);

        public Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken) => Task.Run(() =>
        {
            var status = store.CreateFile(out var handle, out _, ToSmb(remotePath),
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SmbFileAttributes.Normal, ShareAccess.Read,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            Check(status, $"abrir {remotePath}");

            try
            {
                using var output = File.Create(localFile);
                long offset = 0;
                var chunkSize = (int)Math.Min(client.MaxReadSize, 1 << 20);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    status = store.ReadFile(out var data, handle, offset, chunkSize);
                    if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
                    {
                        break;
                    }

                    Check(status, $"leer {remotePath}");
                    output.Write(data);
                    offset += data.Length;
                }
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken);

        public Task DeleteAsync(string remotePath, CancellationToken cancellationToken) => Task.Run(() =>
        {
            var status = store.CreateFile(out var handle, out _, ToSmb(remotePath),
                AccessMask.DELETE | AccessMask.SYNCHRONIZE, SmbFileAttributes.Normal, ShareAccess.None,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            Check(status, $"abrir {remotePath} para eliminar");
            try
            {
                Check(store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }), $"eliminar {remotePath}");
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken);

        public ValueTask DisposeAsync()
        {
            store.Disconnect();
            client.Logoff();
            client.Disconnect();
            return ValueTask.CompletedTask;
        }

        private void EnsureDirectory(string path)
        {
            var current = string.Empty;
            foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? part : current + "\\" + part;
                var status = store.CreateFile(out var handle, out _, current,
                    AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SmbFileAttributes.Directory, ShareAccess.Read | ShareAccess.Write,
                    CreateDisposition.FILE_OPEN_IF, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                Check(status, $"crear carpeta {current}");
                store.CloseFile(handle);
            }
        }

        private static string ToSmb(string path) => path.Replace('/', '\\').Trim('\\');

        private static void Check(NTStatus status, string action)
        {
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"SMB: no se pudo {action}: {SmbErrors.Describe(status)}");
            }
        }
    }
}

public sealed class SmbSource() : FileSessionSource(new SmbSessionFactory());

public sealed class SmbDestination() : FileSessionDestination(new SmbSessionFactory());
