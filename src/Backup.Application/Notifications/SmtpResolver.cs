using Backup.Application.Abstractions;
using Backup.Domain.Notifications;
using Microsoft.Extensions.Options;

namespace Backup.Application.Notifications;

/// <summary>SMTP de la plataforma, configurado por el operador (sección <c>Smtp</c> / variables <c>Smtp__*</c>).</summary>
public sealed class PlatformSmtpOptions
{
    public const string SectionName = "Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "BackupHub";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>
/// Decide con qué servidor sale cada correo:
/// alertas → el que eligió el tenant (propio o de plataforma);
/// correos de cuenta (invitaciones, recuperación) → plataforma y, si no hay, el propio del tenant.
/// </summary>
public sealed class SmtpResolver(INotificationSettingsRepository repository, IOptionsMonitor<PlatformSmtpOptions> platform)
{
    public SmtpServer? Platform => platform.CurrentValue is { IsConfigured: true } o
        ? new SmtpServer(o.Host!.Trim(), o.Port, o.Security, o.Username, o.Password, o.FromAddress!.Trim(), o.FromName)
        : null;

    /// <summary>Remitente del SMTP de plataforma, para mostrarlo en la UI (sin exponer credenciales).</summary>
    public string? PlatformSender => Platform?.FromAddress;

    public SmtpServer? ForAlerts(NotificationSettings settings) => settings.UsePlatformSmtp ? Platform : settings.OwnSmtp;

    public async Task<SmtpServer?> ForAccountAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Platform ?? (await repository.GetAsync(tenantId, cancellationToken))?.OwnSmtp;
}
