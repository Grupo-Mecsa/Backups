namespace Backup.Application.Providers;

/// <param name="Settings">Configuración del destino de la restauración (datos de acceso, carpeta o base, opciones).</param>
/// <param name="ArtifactFile">Respaldo ya descifrado y descomprimido (.zip, .dump, .sql...).</param>
/// <param name="WorkingDirectory">Carpeta temporal propia de esta restauración.</param>
public sealed record RestoreContext(ProviderSettings Settings, string ArtifactFile, string WorkingDirectory, IRunLog Log);

/// <summary>
/// Proveedor que puede recibir una restauración: una carpeta (FTP, SMB, local...) para un .zip, o un motor de base
/// de datos para su dump. Reutiliza los campos del proveedor (conexión, carpeta, base) y agrega sus opciones.
/// </summary>
public interface IRestoreTarget : IProvider
{
    /// <param name="artifactFileName">Nombre del respaldo ya descifrado y descomprimido, p. ej. "erp_20260930.dump".</param>
    bool CanRestore(string artifactFileName);

    /// <summary>Campos del formulario de restauración: los del proveedor que aplican más las opciones propias.</summary>
    IReadOnlyList<SettingField> RestoreFields { get; }

    /// <summary>Resumen legible de a dónde se restaura, sin secretos (queda en el historial).</summary>
    string DescribeTarget(ProviderSettings settings);

    /// <returns>True si terminó limpio; false si terminó con errores no fatales (p. ej. objetos que ya existían).</returns>
    Task<bool> RestoreAsync(RestoreContext context, CancellationToken cancellationToken);
}
