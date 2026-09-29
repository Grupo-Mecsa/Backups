namespace Backup.Domain.Notifications;

public enum SmtpSecurity
{
    /// <summary>Negocia TLS con STARTTLS si el servidor lo ofrece (puerto 587).</summary>
    Auto = 0,
    StartTls = 1,

    /// <summary>TLS implícito desde la conexión (puerto 465).</summary>
    SslOnConnect = 2,
    None = 3,
}

/// <summary>Servidor SMTP con el que se envía un correo (del tenant o de la plataforma).</summary>
public sealed record SmtpServer(
    string Host,
    int Port,
    SmtpSecurity Security,
    string? Username,
    string? Password,
    string FromAddress,
    string FromName);

/// <summary>Configuración de alertas por correo de un tenant.</summary>
public sealed class NotificationSettings
{
    public Guid TenantId { get; set; }
    public bool Enabled { get; set; }

    /// <summary>Envía con el SMTP de la plataforma (variables <c>Smtp__*</c>) en lugar de uno propio.</summary>
    public bool UsePlatformSmtp { get; set; }

    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "BackupHub";

    /// <summary>Destinatarios separados por coma o punto y coma.</summary>
    public string? Recipients { get; set; }

    public bool NotifyOnFailure { get; set; } = true;
    public bool NotifyOnSuccess { get; set; }

    public IReadOnlyList<string> RecipientList =>
        (Recipients ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>El tenant tiene un servidor SMTP propio completo.</summary>
    public bool HasOwnSmtp => !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(FromAddress);

    public SmtpServer? OwnSmtp => HasOwnSmtp
        ? new SmtpServer(SmtpHost!.Trim(), SmtpPort, Security, Username, Password, FromAddress!.Trim(), FromName)
        : null;
}
