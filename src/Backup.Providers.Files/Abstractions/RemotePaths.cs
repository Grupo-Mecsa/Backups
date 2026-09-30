using Backup.Application.Providers;

namespace Backup.Providers.Files.Abstractions;

internal static class RemotePaths
{
    /// <summary>
    /// Convierte una ruta en absoluta: si empieza con '/' se respeta; si no, se resuelve desde la carpeta
    /// inicial del usuario (como haría un cliente FTP/SFTP al escribir una ruta relativa).
    /// </summary>
    public static string Resolve(string home, string path)
    {
        var clean = path.Replace('\\', '/');
        if (clean.StartsWith('/'))
        {
            return clean;
        }

        var baseDir = "/" + home.Trim('/');
        return clean.Trim('/').Length == 0 ? baseDir : $"{baseDir.TrimEnd('/')}/{clean.Trim('/')}";
    }

    /// <summary>Une carpeta y nombre conservando la '/' inicial: "/" + "mnt" = "/mnt" (no "mnt", que sería relativa).</summary>
    public static string Combine(string directory, string name) =>
        directory.StartsWith('/')
            ? "/" + ProviderHelpers.CombineRemote(directory.Trim('/'), name)
            : ProviderHelpers.CombineRemote(directory, name);

    /// <summary>
    /// Carpeta remota configurada: "/" se conserva (raíz del servidor); vacío = carpeta inicial del usuario.
    /// </summary>
    public static string Root(string? configured)
    {
        var value = (configured ?? string.Empty).Trim();
        return value.Length > 0 && value.Trim('/').Length == 0 ? "/" : value.TrimEnd('/');
    }
}
