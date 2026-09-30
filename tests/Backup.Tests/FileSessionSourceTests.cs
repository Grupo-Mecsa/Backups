using System.IO.Compression;
using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;

namespace Backup.Tests;

/// <summary>
/// El origen de archivos debe aplicar la selección del explorador (rutas absolutas) aunque el usuario FTP/SFTP
/// empiece en otra carpeta: antes "/mnt/.../trashbox" excluida se copiaba igual porque se comparaba "array1/...".
/// </summary>
public sealed class FileSessionSourceTests : IDisposable
{
    private static readonly string[] Files =
    [
        "/mnt/array1/Inventario/a.xlsx",
        "/mnt/array1/Inventario/trashbox/b.xlsx",
        "/mnt/array1/Otro/c.xlsx",
    ];

    private readonly string _work = Directory.CreateTempSubdirectory("fss-").FullName;

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    public async Task Selection_FromExplorer_IsAppliedWhenHomeIsNotRoot(string remotePath)
    {
        var settings = new ProviderSettings(new Dictionary<string, string?>
        {
            ["remotePath"] = remotePath,
            ["selection"] = "-\n+mnt/array1/Inventario\n-mnt/array1/Inventario/trashbox",
        });
        var source = new FakeSource();

        var artifact = await source.CreateArtifactAsync(new SourceContext(settings, _work, "prueba", NullRunLog.Instance), CancellationToken.None);

        using var zip = ZipFile.OpenRead(artifact.FilePath);
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(n => !n.EndsWith('/')).ToList();
        Assert.Equal(["mnt/array1/Inventario/a.xlsx"], names);
    }

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private sealed class FakeSource() : FileSessionSource(new FakeFactory());

    /// <summary>Servidor en memoria que, como FTP/SFTP, resuelve las rutas relativas desde la carpeta inicial "/mnt".</summary>
    private sealed class FakeFactory : IFileSessionFactory
    {
        public ProviderDescriptor CreateDescriptor(ProviderRole role) =>
            new("fake", "Fake", string.Empty, ProviderCategory.FileTransfer, "server", []);

        public Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSession>(new FakeSession());

        public string RootPath(ProviderSettings settings) =>
            settings.Get("remotePath") is { } value && value.Trim('/').Length == 0 ? "/" : (settings.Get("remotePath") ?? string.Empty).TrimEnd('/');

        public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSession : IFileSession
    {
        public string HomeDirectory => "/mnt";

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var directory = Absolute(remoteDirectory).TrimEnd('/');
            var children = Files
                .Where(f => f.StartsWith(directory + "/", StringComparison.Ordinal))
                .Select(f => f[(directory.Length + 1)..].Split('/')[0])
                .Distinct()
                .Select(name =>
                {
                    var full = $"{directory}/{name}";
                    var path = remoteDirectory.StartsWith('/') ? full : ProviderHelpers.CombineRemote(remoteDirectory, name);
                    return new RemoteEntry(path, name, !Files.Contains(full), null, Files.Contains(full) ? 1 : null);
                })
                .ToList();
            return Task.FromResult<IReadOnlyList<RemoteEntry>>(children);
        }

        public Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(localFile, Absolute(remotePath), cancellationToken);

        public Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string remotePath, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private string Absolute(string path) =>
            path.StartsWith('/') ? path : path.Length == 0 ? HomeDirectory : $"{HomeDirectory}/{path}";
    }
}
