namespace Backup.Domain.Runs;

/// <summary>Registro de una ejecución de un trabajo de respaldo.</summary>
public sealed class BackupRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid JobId { get; set; }
    public string JobName { get; set; } = string.Empty;

    public RunTrigger Trigger { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Running;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public string? ArtifactName { get; set; }
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public int DeletedByRetention { get; set; }

    public string? Error { get; set; }
    public string Log { get; set; } = string.Empty;

    public TimeSpan? Duration => FinishedAt - StartedAt;
}
