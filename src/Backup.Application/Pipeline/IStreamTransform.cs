using Backup.Domain.Jobs;

namespace Backup.Application.Pipeline;

/// <summary>
/// Transformación aplicada al artefacto antes de subirlo (compresión, cifrado...).
/// Nuevas transformaciones se agregan registrando otra implementación (OCP).
/// </summary>
public interface IStreamTransform
{
    /// <summary>Orden de aplicación sobre los datos: menor primero (comprimir antes de cifrar).</summary>
    int Order { get; }

    bool AppliesTo(BackupJob job);

    /// <summary>Extensión que se agrega al nombre del archivo, p. ej. ".gz".</summary>
    string GetExtension(BackupJob job);

    /// <summary>
    /// Devuelve un stream de escritura que transforma los datos y los escribe en <paramref name="output"/>.
    /// Al cerrarse debe vaciar sus bloques finales pero dejar <paramref name="output"/> abierto.
    /// </summary>
    Stream WrapWrite(Stream output, BackupJob job);
}
