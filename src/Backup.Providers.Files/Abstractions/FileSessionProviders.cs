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
public abstract class FileSessionDestination(IFileSessionFactory factory) : IBackupDestination, IConnectionTester, IFolderBrowser
{
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
public abstract class FileSessionSource(IFileSessionFactory factory) : IBackupSource, IConnectionTester, IFolderBrowser
{
    public ProviderDescriptor Descriptor { get; } = factory.CreateDescriptor(ProviderRole.Source);

    /// <summary>Campos de filtrado comunes a los orígenes de archivos.</summary>
    /// <param name="rootField">Campo que contiene la carpeta raíz sobre la que se aplica la selección.</param>
    public static IEnumerable<SettingField> FilterFields(string rootField) =>
    [
        SettingField.Selection("selection", rootField),
        SettingField.Text("include", "Incluir por patrón", placeholder: "*.pdf, *.docx", help: "Patrones de nombre separados por coma. Vacío = todo."),
        SettingField.Text("exclude", "Excluir por patrón", placeholder: "*.tmp, ~*"),
        SettingField.Toggle("recursive", "Incluir subcarpetas", true),
    ];

    /// <summary>Combina la selección del explorador con los patrones de nombre.</summary>
    public static (PathSelection Selection, Func<string, bool> Include, Func<string, bool> Exclude, bool Recursive) Filters(ProviderSettings settings)
    {
        var excludePatterns = settings.GetList("exclude");
        return (
            PathSelection.Parse(settings.GetRaw("selection")),
            ProviderHelpers.GlobFilter(settings.GetList("include")),
            excludePatterns.Count == 0 ? (_ => false) : ProviderHelpers.GlobFilter(excludePatterns),
            settings.GetBool("recursive", true));
    }

    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var root = factory.RootPath(settings);
        var (selection, include, exclude, recursive) = Filters(settings);
        var staging = Directory.CreateDirectory(Path.Combine(context.WorkingDirectory, "files")).FullName;

        await using var session = await factory.OpenAsync(settings, cancellationToken);
        var pending = new Stack<string>([root]);
        var count = 0;
        long bytes = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in await session.ListAsync(directory, cancellationToken))
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
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await session.DownloadAsync(entry.Path, target, cancellationToken);
                count++;
                bytes += entry.Size ?? 0;
            }
        }

        context.Log.Info($"{count} archivos descargados ({Application.ByteSize.Format(bytes)}).");
        return await ProviderHelpers.ZipDirectoryAsync(staging, context, cancellationToken);
    }

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
