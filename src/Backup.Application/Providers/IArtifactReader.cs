namespace Backup.Application.Providers;

/// <summary>Destino que permite descargar un respaldo ya subido (para revisarlo o descargarlo desde la web).</summary>
public interface IArtifactReader
{
    /// <param name="objectName">Ruta relativa a la raíz del destino, tal como se subió.</param>
    Task DownloadAsync(DestinationContext context, string objectName, string localFile, CancellationToken cancellationToken);
}
