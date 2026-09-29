namespace Backup.Application;

public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Directorio temporal donde se generan los artefactos antes de subirlos.</summary>
    public string WorkingDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "backup-work");

    /// <summary>Cantidad máxima de respaldos ejecutándose en paralelo.</summary>
    public int MaxConcurrentRuns { get; set; } = 2;

    /// <summary>Días que se conserva el historial de ejecuciones. 0 = para siempre.</summary>
    public int RunHistoryDays { get; set; } = 90;
}
