namespace Backup.Domain.Jobs;

/// <summary>
/// Definición de un trabajo de respaldo: de dónde se leen los datos, cómo se transforman,
/// a dónde se envían y cuándo se ejecuta.
/// </summary>
public sealed class BackupJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;

    public ProviderBinding Source { get; set; } = new();
    public ProviderBinding Destination { get; set; } = new();

    /// <summary>A dónde se restaura por defecto su respaldo (servidor y base, o carpeta). Null = se elige al restaurar.</summary>
    public ProviderBinding? RestoreTarget { get; set; }

    /// <summary>Expresión cron de 5 campos (min hora día mes díaSemana). Null = solo manual.</summary>
    public string? Schedule { get; set; }
    public string TimeZone { get; set; } = "UTC";

    public CompressionKind Compression { get; set; } = CompressionKind.GZip;

    /// <summary>Contraseña para cifrado AES-256. Null o vacío = sin cifrado.</summary>
    public string? EncryptionPassphrase { get; set; }

    public RetentionPolicy Retention { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsEncrypted => !string.IsNullOrEmpty(EncryptionPassphrase);
    public bool IsScheduled => Enabled && !string.IsNullOrWhiteSpace(Schedule);
}
