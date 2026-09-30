using System.IO.Compression;
using Backup.Application.Providers;
using Backup.Providers.Files.Abstractions;

namespace Backup.Providers.Files.Local;

/// <summary>
/// Carpeta local o volumen montado (en Docker: bind mount, NFS, CIFS...).
/// Como origen comprime directamente sin copiar a un staging intermedio.
/// </summary>
public sealed class LocalFolderSource : IBackupSource, IConnectionTester, IFolderBrowser, IFolderCreator
{
    public ProviderDescriptor Descriptor { get; } = new(
        "local",
        "Carpeta local",
        "Carpeta o archivo en el servidor o en un volumen montado.",
        ProviderCategory.Local,
        "folder",
        [
            SettingField.Text("path", "Ruta", required: true, placeholder: "/mnt/datos o C:\\Datos", help: "Carpeta o archivo individual.")
                .Browsable(BrowseMode.FolderOrFile),
            .. FileSessionSource.FilterFields("path"),
        ]);

    /// <summary>Una carpeta se empaqueta en .zip; un archivo suelto se copia con su propia extensión.</summary>
    public string? ArtifactExtension(ProviderSettings settings) =>
        settings.Get("path") is { } path && File.Exists(path) ? Path.GetExtension(path) : ".zip";

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        Task.FromResult(LocalBrowser.Browse(path));

    public Task<string> CreateFolderAsync(ProviderSettings settings, string parentPath, string name, CancellationToken cancellationToken) =>
        Task.FromResult(LocalBrowser.CreateFolder(parentPath, name));

    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var path = settings.Require("path");

        if (File.Exists(path))
        {
            var extension = Path.GetExtension(path);
            var copy = Path.Combine(context.WorkingDirectory, context.BaseName + extension);
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true))
            await using (var output = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            return new BackupArtifact(copy, extension);
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"No existe la ruta {path}.");
        }

        var (selection, include, exclude, recursive) = FileSessionSource.Filters(settings);
        var zipPath = Path.Combine(context.WorkingDirectory, context.BaseName + ".zip");
        var count = 0;
        var skipped = 0;
        await using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        await using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (var file in EnumerateSelected(path, selection, exclude, recursive, context.Log))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                var name = Path.GetFileName(file);
                if (!include(name) || exclude(name))
                {
                    continue;
                }

                try
                {
                    var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(file);
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
                    await using var output = await entry.OpenAsync(cancellationToken);
                    await input.CopyToAsync(output, cancellationToken);
                    count++;
                }
                catch (IOException ex)
                {
                    skipped++;
                    context.Log.Warn($"Omitido {relative}: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    skipped++;
                    context.Log.Warn($"Omitido {relative}: {ex.Message}");
                }
            }
        }

        context.Log.Info($"{count} archivos comprimidos{(skipped > 0 ? $", {skipped} omitidos" : string.Empty)}.");
        return new BackupArtifact(zipPath, ".zip");
    }

    /// <summary>Recorre la carpeta sin entrar en subcarpetas excluidas por la selección o por patrón.</summary>
    private static IEnumerable<string> EnumerateSelected(
        string root, PathSelection selection, Func<string, bool> exclude, bool recursive, IRunLog log)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files, directories;
            try
            {
                files = Directory.GetFiles(directory, "*", options);
                directories = recursive ? Directory.GetDirectories(directory, "*", options) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"No se pudo leer {directory}: {ex.Message}");
                continue;
            }

            foreach (var file in files.Where(f => selection.IsIncluded(Path.GetRelativePath(root, f))))
            {
                yield return file;
            }

            foreach (var sub in directories)
            {
                if (!exclude(Path.GetFileName(sub)) && selection.ShouldDescend(Path.GetRelativePath(root, sub)))
                {
                    pending.Push(sub);
                }
            }
        }
    }

    public Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var path = settings.Require("path");
        return File.Exists(path)
            ? Task.FromResult($"Archivo encontrado ({Application.ByteSize.Format(new FileInfo(path).Length)}).")
            : Directory.Exists(path)
                ? Task.FromResult("Carpeta accesible.")
                : throw new DirectoryNotFoundException($"No existe la ruta {path}.");
    }
}

public sealed class LocalFolderDestination() : FileSessionDestination(new LocalSessionFactory()), IFolderCreator
{
    public Task<string> CreateFolderAsync(ProviderSettings settings, string parentPath, string name, CancellationToken cancellationToken) =>
        Task.FromResult(LocalBrowser.CreateFolder(parentPath, name));
}

internal sealed class LocalSessionFactory : IFileSessionFactory
{
    public ProviderDescriptor CreateDescriptor(ProviderRole role) => new(
        "local",
        "Carpeta local",
        "Carpeta en el servidor o volumen montado (NAS, NFS, disco externo).",
        ProviderCategory.Local,
        "folder",
        [SettingField.Text("path", "Ruta", required: true, placeholder: "/backups o D:\\Backups").Browsable()]);

    public Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult<IFileSession>(new LocalSession(settings.Require("path")));

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        Task.FromResult(LocalBrowser.Browse(path));

    // Las rutas de objeto se resuelven dentro de la sesión, que ya conoce su raíz.
    public string RootPath(ProviderSettings settings) => string.Empty;

    private sealed class LocalSession(string root) : IFileSession
    {
        public async Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken)
        {
            var target = Resolve(remotePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var partial = target + ".partial";
            await using (var input = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            File.Move(partial, target, overwrite: true);
        }

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken)
        {
            var directory = new DirectoryInfo(Resolve(remoteDirectory));
            IReadOnlyList<RemoteEntry> entries = directory.Exists
                ? [.. directory.EnumerateFileSystemInfos().Select(info => new RemoteEntry(
                    ProviderHelpers.CombineRemote(remoteDirectory, info.Name),
                    info.Name,
                    info is DirectoryInfo,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    info is FileInfo file ? file.Length : null))]
                : [];
            return Task.FromResult(entries);
        }

        public Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken)
        {
            File.Copy(Resolve(remotePath), localFile, overwrite: true);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
        {
            File.Delete(Resolve(remotePath));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private string Resolve(string relative)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            return full == fullRoot || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? full
                : throw new UnauthorizedAccessException("Ruta fuera de la carpeta de destino.");
        }
    }
}
