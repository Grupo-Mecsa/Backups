namespace Backup.Domain.Runs;

public enum RunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,

    /// <summary>Terminó y subió el respaldo, pero omitió elementos que no pudo leer (ver bitácora).</summary>
    Warning = 4,
}
