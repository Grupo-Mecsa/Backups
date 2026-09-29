namespace Backup.Domain.Notifications;

/// <summary>Chat de Telegram (privado o grupo) que recibe las alertas de un tenant.</summary>
public sealed class TelegramSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Usuario de BackupHub que vinculó el chat.</summary>
    public string UserId { get; set; } = string.Empty;

    public long ChatId { get; set; }

    /// <summary>Nombre del chat privado (@usuario) o título del grupo, para reconocerlo en la UI.</summary>
    public string ChatTitle { get; set; } = string.Empty;

    public bool IsGroup { get; set; }
    public bool NotifyOnFailure { get; set; } = true;
    public bool NotifyOnSuccess { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
