using System.Diagnostics;
using System.IO.Compression;
using Backup.Application.Providers;

namespace Backup.Providers.Files.Abstractions;

/// <summary>Fábrica de sesiones: lo único que cambia entre protocolos (Template Method + Strategy).</summary>
public interface IFileSessionFactory
{
    ProviderDescriptor CreateDescriptor(ProviderRole role);
    Task<IFileSession> OpenAsync(ProviderSettings settings, CancellationToken cancellationToken);
    string RootPath(ProviderSettings settings);
    Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken);
}

/// <summary>Exploración genérica para protocolos remotos con rutas absolutas tipo "/a/b".</summary>
public static class FileSessionBrowsing
{
    public static async Task<FolderListing> BrowseAsync(
        IFileSessionFactory factory, ProviderSettings settings, string? path, CancellationToken cancellationToken)
    {
        await using var session = await factory.OpenAsync(settings, cancellationToken);
        var current = "/" + (string.IsNullOrWhiteSpace(path) ? session.HomeDirectory : path).Replace('\\', '/').Trim('/');
        var entries = await session.ListAsync(current, cancellationToken);
        return FolderListing.Create(
            current,
            FolderListing.SlashParent(current),
            entries.Select(e => new FolderItem(e.Name, "/" + e.Path.TrimStart('/'), e.IsDirectory, e.Size, e.Modified)));
    }
}

/// <summary>Destino genérico sobre cualquier <see cref="IFileSession"/>.</summary>
public abstract class FileSessionDestination(IFileSessionFactory factory) : IBackupDestination, IConnectionTester, IFolderBrowser, IArtifactReader, IRestoreTarget
{
    private static readonly SettingField OverwriteField = SettingField.Toggle(
        "overwrite", "Sobrescribir archivos existentes", false,
        "Apagado: los archivos que ya existen en la carpeta de destino se dejan como están.");

    public ProviderDescriptor Descriptor { get; } = factory.CreateDescriptor(ProviderRole.Destination);

    public async Task UploadAsync(DestinationContext context, string localFile, string objectName, CancellationToken cancellationToken)
    {
        await using var session = await factory.OpenAsync(context.Settings, cancellationToken);
        await session.UploadAsync(localFile, ProviderHelpers.CombineRemote(factory.RootPath(context.Settings), objectName), cancellationToken);
    }

    public async Task<IReadOnlyList<StoredBackup>> ListAsync(DestinationContext context, string folder, CancellationToken cancellationToken)
    {
        var root = factory.RootPath(context.Settings);
        await using var session = await factory.OpenAsync(context.Settings, cancellationToken);
        var entries = await session.ListAsync(ProviderHelpers.CombineRemote(root, folder), cancellationToken);
        return [.. entries
            .Where(e => !e.IsDirectory)
            .Select(e => new StoredBackup(ProviderHelpers.MakeRelative(root, e.Path), e.Modified, e.Size))];
    }

    public bool CanRestore(string artifactFileName) => artifactFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Datos de acceso, la carpeta donde dejar los archivos y si se sobrescriben.</summary>
    public IReadOnlyList<SettingField> RestoreFields =>
        [.. Descriptor.Fields.Where(f => f.IsConnection || f.Browse != BrowseMode.None), OverwriteField];

    public string DescribeTarget(ProviderSettings settings)
    {
        var folder = factory.RootPath(settings) is { Length: > 0 } configured ? configured : settings.Get("path") ?? string.Empty;
        var server = settings.Get("host");
        return $"{(server is null ? string.Empty : server + ":")}{(folder.Length == 0 ? "/" : folder)}";
    }

    /// <summary>Descomprime el .zip del respaldo y sube cada archivo a la carpeta elegida, respetando la estructura.</summary>
    public async Task<bool> RestoreAsync(RestoreContext context, CancellationToken cancellationToken)
    {
        var extracted = Path.Combine(context.WorkingDirectory, "files");
        context.Log.Info("Descomprimiendo el respaldo...");
        await ZipFile.ExtractToDirectoryAsync(context.ArtifactFile, extracted, overwriteFiles: true, cancellationToken);

        var root = factory.RootPath(context.Settings);
        var overwrite = context.Settings.GetBool("overwrite");
        var files = Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories).ToList();
        context.Log.Info($"{files.Count} archivos para restaurar en {DescribeTarget(context.Settings)}{(overwrite ? " (sobrescribiendo)" : string.Empty)}.");

        await using var session = await factory.OpenAsync(context.Settings, cancellationToken);
        var existing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        int restored = 0, skipped = 0;
        var lastProgress = Stopwatch.GetTimestamp();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(extracted, file).Replace('\\', '/');
            var remote = ProviderHelpers.CombineRemote(root, relative);
            if (!overwrite)
            {
                var slash = remote.LastIndexOf('/');
                var directory = slash < 0 ? string.Empty : remote[..slash];
                if (!existing.TryGetValue(directory, out var names))
                {
                    names = [.. (await session.ListAsync(directory, cancellationToken)).Select(e => e.Name)];
                    existing[directory] = names;
                }

                if (names.Contains(Path.GetFileName(file)))
                {
                    skipped++;
                    continue;
                }
            }

            await session.UploadAsync(file, remote, cancellationToken);
            restored++;
            if (Stopwatch.GetElapsedTime(lastProgress) >= ProgressInterval)
            {
                lastProgress = Stopwatch.GetTimestamp();
                context.Log.Info($"Progreso: {restored + skipped} de {files.Count} archivos.");
            }
        }

        context.Log.Info($"{restored} archivos restaurados{(skipped > 0 ? $", {skipped} ya existían y se dejaron como estaban" : string.Empty)}.");
        return true;
    }

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    public async Task DownloadAsync(DestinationContext context, string objectName, string localFile, CancellationToken cancellationToken)
    {
        await using var session = await factory.OpenAsync(context.Settings, cancellationToken);
        await session.DownloadAsync(ProviderHelpers.CombineRemote(factory.RootPath(context.Settings), objectName), localFile, cancellationToken);
    }

    public async Task DeleteAsync(DestinationContext context, string objectName, CancellationToken cancellationToken)
    {
        await using var session = await factory.OpenAsync(context.Settings, cancellationToken);
        await session.DeleteAsync(ProviderHelpers.CombineRemote(factory.RootPath(context.Settings), objectName), cancellationToken);
    }

    public Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        FileSessionSource.TestAsync(factory, settings, cancellationToken);

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        factory.BrowseAsync(settings, path, cancellationToken);
}

/// <summary>Origen genérico: descarga recursivamente una carpeta remota y la empaqueta en .zip.</summary>
public abstract class FileSessionSource(IFileSessionFactory factory) : IBackupSource, IStreamingSource, IConnectionTester, IFolderBrowser
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    public ProviderDescriptor Descriptor { get; } = factory.CreateDescriptor(ProviderRole.Source);

    public string? ArtifactExtension(ProviderSettings settings) => ".zip";

    /// <summary>Campos de filtrado comunes a los orígenes de archivos.</summary>
    /// <param name="rootField">Campo que contiene la carpeta raíz sobre la que se aplica la selección.</param>
    public static IEnumerable<SettingField> FilterFields(string rootField) =>
    [
        SettingField.Selection("selection", rootField),
        SettingField.Text("include", "Incluir por patrón", placeholder: "*.pdf, *.docx", help: "Patrones de nombre separados por coma. Vacío = todo."),
        SettingField.Text("exclude", "Excluir por patrón", placeholder: "*.tmp, ~*"),
        SettingField.Toggle("recursive", "Incluir subcarpetas", true),
        SettingField.Toggle("skipTemporary", "Omitir archivos temporales", true,
            "Bloqueos de Office (~$*), *.tmp, descargas a medias, Thumbs.db, .DS_Store y similares."),
    ];

    /// <summary>
    /// Archivos y carpetas que crean el sistema o los programas mientras trabajan y no vale la pena respaldar:
    /// bloqueos de Office/LibreOffice, temporales, descargas incompletas, swap de editores y metadatos de Windows/macOS/NAS.
    /// </summary>
    public static readonly IReadOnlyList<string> TemporaryPatterns =
    [
        "~$*", "~*.tmp", "*.tmp", "*.temp", ".~lock.*#",
        "*.part", "*.partial", "*.crdownload", "*.download",
        "*.swp", "*.swo",
        "Thumbs.db", "ehthumbs.db", ".DS_Store", "._*",
        ".AppleDouble", ".TemporaryItems", "@eaDir",
    ];

    /// <summary>Combina la selección del explorador con los patrones de nombre.</summary>
    public static (PathSelection Selection, Func<string, bool> Include, Func<string, bool> Exclude, bool Recursive) Filters(ProviderSettings settings)
    {
        var excludePatterns = settings.GetList("exclude").ToList();
        if (settings.GetBool("skipTemporary", true))
        {
            excludePatterns.AddRange(TemporaryPatterns);
        }

        return (
            PathSelection.Parse(settings.GetRaw("selection")),
            ProviderHelpers.GlobFilter(settings.GetList("include")),
            excludePatterns.Count == 0 ? (_ => false) : ProviderHelpers.GlobFilter(excludePatterns),
            settings.GetBool("recursive", true));
    }

    public string StreamExtension => ".zip";

    /// <summary>Genera el .zip como archivo (para quien no use <see cref="WriteArtifactAsync"/>).</summary>
    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(context.WorkingDirectory, context.BaseName + ".zip");
        await using (var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await WriteArtifactAsync(context, file, cancellationToken);
        }

        return new BackupArtifact(zipPath, ".zip");
    }

    /// <summary>
    /// Escribe el .zip directamente en <paramref name="output"/> (el pipeline de compresión, cifrado y hash): cada archivo
    /// se descarga a un temporal, se agrega al zip y se borra. Así el disco solo necesita el paquete final y el archivo en
    /// curso, en lugar de la carpeta descargada, el .zip y el paquete a la vez.
    /// </summary>
    public async Task WriteArtifactAsync(SourceContext context, Stream output, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var root = factory.RootPath(settings);
        var (selection, include, exclude, recursive) = Filters(settings);

        // Raíz virtual: solo sirve para validar que ninguna ruta del zip se salga de la carpeta de origen.
        var staging = Path.GetFullPath(Path.Combine(context.WorkingDirectory, "files"));
        var temp = Path.Combine(context.WorkingDirectory, "descarga.tmp");
        var added = new HashSet<string>(StringComparer.Ordinal);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        await using var session = await factory.OpenAsync(settings, cancellationToken);

        // Sin carpeta configurada se parte de la carpeta inicial del usuario, en ruta absoluta: el explorador del
        // asistente también muestra rutas absolutas, y la selección (incluir/excluir) se guarda relativa a ellas.
        var start = root.Length > 0 || session.HomeDirectory == "/" ? root : session.HomeDirectory;
        context.Log.Info($"Carpeta de origen: {(start.Length == 0 ? "/" : start)}{(recursive ? " (con subcarpetas)" : string.Empty)}{(settings.GetBool("skipTemporary", true) ? ", sin archivos temporales" : string.Empty)}.");
        if (!selection.IsEmpty)
        {
            context.Log.Info($"Selección: {string.Join(", ", selection.Included.Select(p => "+" + (p.Length == 0 ? "/" : p)).Concat(selection.Excluded.Select(p => "-" + (p.Length == 0 ? "/" : p))))}");
        }

        var pending = new Stack<string>([start]);
        var count = 0;
        long bytes = 0;
        var lastProgress = Stopwatch.GetTimestamp();

        // Una línea cada tanto para que la bitácora en vivo muestre que la descarga avanza.
        void ReportProgress(string directory)
        {
            if (Stopwatch.GetElapsedTime(lastProgress) < ProgressInterval)
            {
                return;
            }

            lastProgress = Stopwatch.GetTimestamp();
            context.Log.Info($"Progreso: {count} archivos ({Application.ByteSize.Format(bytes)}) · {pending.Count} carpetas pendientes · en {(directory.Length == 0 ? "/" : directory)}");
        }

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            ReportProgress(directory);
            IReadOnlyList<RemoteEntry> entries;
            try
            {
                entries = await session.ListAsync(directory, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Log.Error($"No se pudo listar la carpeta {directory}.");
                throw;
            }

            if (entries.Count == 0 && directory != start && !await session.DirectoryExistsAsync(directory, cancellationToken))
            {
                context.Log.Omit($"Carpeta omitida (aparece en el listado pero el servidor no deja abrirla): {directory}");
            }

            foreach (var entry in entries)
            {
                var relative = PathSelection.Relative(root, entry.Path);
                if (entry.IsDirectory)
                {
                    if (recursive && !exclude(entry.Name) && selection.ShouldDescend(relative))
                    {
                        pending.Push(entry.Path);
                    }

                    continue;
                }

                if (!selection.IsIncluded(relative) || !include(entry.Name) || exclude(entry.Name))
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(staging, relative));
                if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    context.Log.Warn($"Omitido (ruta fuera de la carpeta de origen): {entry.Path}");
                    continue;
                }

                if (entry.Name.Contains("..", StringComparison.Ordinal))
                {
                    context.Log.Warn($"Nombre con '..' copiado tal cual (revisar): {entry.Path}");
                }

                var entryName = Path.GetRelativePath(staging, target).Replace('\\', '/');
                if (!added.Add(entryName))
                {
                    continue; // el servidor lo listó dos veces
                }

                try
                {
                    await session.DownloadAsync(entry.Path, temp, cancellationToken);
                }
                catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
                {
                    // Un archivo que desapareció o no se puede leer no debe tumbar todo el respaldo.
                    File.Delete(temp);
                    context.Log.Omit($"Omitido {entry.Path}: {ex.Message}");
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    context.Log.Error($"Falló la descarga de {entry.Path} (llevaba {count} archivos descargados).");
                    throw;
                }

                var zipEntry = zip.CreateEntry(entryName, AlreadyCompressed(entry.Name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                if (entry.Modified is { } modified && modified.Year is >= 1980 and <= 2107)
                {
                    zipEntry.LastWriteTime = modified;
                }

                await using (var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                await using (var entryStream = zipEntry.Open())
                {
                    await input.CopyToAsync(entryStream, cancellationToken);
                }

                File.Delete(temp);
                count++;
                bytes += entry.Size ?? 0;
                foreach (var note in session.DrainNotes())
                {
                    context.Log.Info(note);
                }

                ReportProgress(directory);
            }
        }

        context.Log.Info($"{count} archivos descargados ({Application.ByteSize.Format(bytes)}).");
    }

    /// <summary>Formatos que ya vienen comprimidos: volver a comprimirlos gasta CPU sin ahorrar espacio.</summary>
    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic",
        ".mp4", ".mov", ".avi", ".mkv", ".mp3", ".m4a", ".aac", ".ogg",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp",
    };

    private static bool AlreadyCompressed(string name) => CompressedExtensions.Contains(Path.GetExtension(name));

    public Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        TestAsync(factory, settings, cancellationToken);

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        factory.BrowseAsync(settings, path, cancellationToken);

    internal static async Task<string> TestAsync(IFileSessionFactory factory, ProviderSettings settings, CancellationToken cancellationToken)
    {
        await using var session = await factory.OpenAsync(settings, cancellationToken);
        var root = factory.RootPath(settings);
        var entries = await session.ListAsync(root, cancellationToken);
        return $"Conexión exitosa. '{(root.Length == 0 ? "/" : root)}' contiene {entries.Count} elementos.";
    }
}
