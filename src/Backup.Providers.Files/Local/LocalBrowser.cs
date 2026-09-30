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

    /// <summary>Crea <paramref name="name"/> dentro de <paramref name="parentPath"/>; el nombre no puede ser una ruta.</summary>
    public static string CreateFolder(string parentPath, string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0 || clean is "." or ".." || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || clean.Contains('/') || clean.Contains('\\'))
        {
            throw new ArgumentException("Nombre de carpeta no válido: no puede estar vacío ni contener / \\ : * ? \" < > |.");
        }

        if (string.IsNullOrWhiteSpace(parentPath) || !Directory.Exists(parentPath))
        {
            throw new DirectoryNotFoundException("Abre primero la carpeta donde quieres crearla.");
        }

        var target = Path.Combine(parentPath, clean);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"Ya existe «{clean}» en esta carpeta.");
        }

        return Directory.CreateDirectory(target).FullName;
    }
}
