namespace Backup.Domain.Runs;

/// <summary>Registro de una restauración: qué respaldo se restauró, a dónde, quién la pidió y cómo terminó.</summary>
public sealed class RestoreOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Ejecución cuyo respaldo se restauró.</summary>
    public Guid RunId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string ArtifactName { get; set; } = string.Empty;

    public string TargetProvider { get; set; } = string.Empty;

    /// <summary>A dónde se restauró, legible y sin secretos (p. ej. "PostgreSQL · ventas @ db-pruebas:5432").</summary>
    public string TargetSummary { get; set; } = string.Empty;

    public string? RequestedBy { get; set; }

    /// <summary>Running, Succeeded, Warning (terminó con errores ignorados), Failed o Cancelled.</summary>
    public RunStatus Status { get; set; } = RunStatus.Running;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public string? Error { get; set; }
    public string Log { get; set; } = string.Empty;

    public TimeSpan? Duration => FinishedAt - StartedAt;
}
