using Backup.Application.Providers;

namespace Backup.Providers.Files.Local;

/// <summary>Explora el sistema de archivos del servidor (o del contenedor, con sus volúmenes montados).</summary>
internal static class LocalBrowser
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System,
    };

    public static FolderListing Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (OperatingSystem.IsWindows())
            {
                // Raíz virtual: unidades disponibles.
                return FolderListing.Create(string.Empty, null, DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new FolderItem(d.Name.TrimEnd('\\'), d.RootDirectory.FullName, true)));
            }

            path = "/";
        }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            // Si apunta a un archivo, mostrar la carpeta que lo contiene.
            if (File.Exists(path) && directory.Parent is { } containing)
            {
                directory = containing;
            }
            else
            {
                throw new DirectoryNotFoundException($"No existe la carpeta {path}.");
            }
        }

        var items = directory.EnumerateFileSystemInfos("*", Options).Select(info => new FolderItem(
            info.Name,
            info.FullName,
            info is DirectoryInfo,
            info is FileInfo file ? file.Length : null,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));

        var parent = directory.Parent?.FullName ?? (OperatingSystem.IsWindows() ? string.Empty : null);
        return FolderListing.Create(directory.FullName, parent, items);
    }
}
