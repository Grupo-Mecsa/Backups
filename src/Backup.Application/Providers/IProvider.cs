namespace Backup.Application.Providers;

/// <summary>Contrato base de todo proveedor: se identifica y se autodescribe.</summary>
public interface IProvider
{
    ProviderDescriptor Descriptor { get; }
}

/// <summary>Capacidad opcional (ISP): proveedores que pueden probar su conexión.</summary>
public interface IConnectionTester
{
    /// <returns>Mensaje descriptivo del resultado exitoso.</returns>
    Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken);
}

/// <summary>Origen de datos: genera un único artefacto local (dump, .bak, .zip...).</summary>
public interface IBackupSource : IProvider
{
    Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken);

    /// <summary>Extensión del artefacto que producirá con esta configuración (".zip", ".dump"...). Null = no se sabe de antemano.</summary>
    string? ArtifactExtension(ProviderSettings settings) => null;
}

/// <summary>Destino de almacenamiento para los respaldos.</summary>
public interface IBackupDestination : IProvider
{
    /// <param name="objectName">Ruta relativa con '/' como separador, p. ej. "mi-job/mi-job_20260101_020000.sql.gz".</param>
    Task UploadAsync(DestinationContext context, string localFile, string objectName, CancellationToken cancellationToken);

    /// <summary>Lista los respaldos existentes bajo una carpeta relativa a la raíz del destino.</summary>
    Task<IReadOnlyList<StoredBackup>> ListAsync(DestinationContext context, string folder, CancellationToken cancellationToken);

    Task DeleteAsync(DestinationContext context, string objectName, CancellationToken cancellationToken);
}

/// <param name="BaseName">Nombre sugerido (sin extensión) para el artefacto.</param>
public sealed record SourceContext(ProviderSettings Settings, string WorkingDirectory, string BaseName, IRunLog Log);

public sealed record DestinationContext(ProviderSettings Settings, IRunLog Log);

/// <summary>Archivo producido por un origen, listo para transformarse y subirse.</summary>
/// <param name="FilePath">Ruta local del archivo generado.</param>
/// <param name="Extension">Extensión lógica del contenido, p. ej. ".sql", ".bak", ".zip".</param>
public sealed record BackupArtifact(string FilePath, string Extension);

/// <param name="ObjectName">Ruta relativa a la raíz del destino, con '/' como separador.</param>
public sealed record StoredBackup(string ObjectName, DateTimeOffset? LastModified, long? Size);
