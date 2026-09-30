namespace Backup.Providers.Files.Abstractions;

/// <summary>
/// Sesión sobre un sistema de archivos remoto (FTP, SFTP, SMB, disco local).
/// Las rutas usan '/' como separador; cada implementación las adapta a su protocolo.
/// </summary>
public interface IFileSession : IAsyncDisposable
{
    /// <summary>Carpeta en la que el servidor deja al usuario al iniciar sesión (punto de partida del explorador).</summary>
    string HomeDirectory => "/";

    /// <summary>Sube un archivo creando las carpetas intermedias necesarias.</summary>
    Task UploadAsync(string localFile, string remotePath, CancellationToken cancellationToken);

    /// <summary>Lista el contenido directo de una carpeta. Carpeta inexistente = lista vacía.</summary>
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Si la carpeta se puede abrir. Distingue una carpeta vacía de una que el servidor listó pero no deja abrir
    /// (p. ej. nombres con tildes en otra codificación). Por defecto se asume que sí.
    /// </summary>
    Task<bool> DirectoryExistsAsync(string remoteDirectory, CancellationToken cancellationToken) => Task.FromResult(true);

    Task DownloadAsync(string remotePath, string localFile, CancellationToken cancellationToken);

    Task DeleteAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>Avisos acumulados para la bitácora (se devuelven una sola vez).</summary>
    IEnumerable<string> DrainNotes() => [];
}

/// <param name="Path">Ruta completa con '/' como separador.</param>
public sealed record RemoteEntry(string Path, string Name, bool IsDirectory, DateTimeOffset? Modified, long? Size);
