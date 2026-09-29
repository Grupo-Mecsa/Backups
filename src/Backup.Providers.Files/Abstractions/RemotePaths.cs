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
}
