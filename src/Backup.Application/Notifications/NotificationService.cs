using System.Net.Mail;
using Backup.Application.Abstractions;
using Backup.Application.Jobs;
using Backup.Application.Security;
using Backup.Domain.Notifications;

namespace Backup.Application.Notifications;

/// <summary>Configuración de alertas por correo del tenant actual (solo administradores).</summary>
public sealed class NotificationService(
    INotificationSettingsRepository repository,
    SmtpResolver smtpResolver,
    IEmailSender sender,
    ICurrentUser user)
{
    /// <summary>Remitente del SMTP de plataforma, o <c>null</c> si el operador no lo configuró.</summary>
    public string? PlatformSender => smtpResolver.PlatformSender;

    /// <summary>Configuración sin la contraseña SMTP; <c>HasPassword</c> indica si hay una guardada.</summary>
    public async Task<(NotificationSettings Settings, bool HasPassword)> GetAsync(CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var settings = await repository.GetAsync(user.TenantId, cancellationToken)
            ?? new NotificationSettings { TenantId = user.TenantId, UsePlatformSmtp = smtpResolver.Platform is not null };
        var hasPassword = !string.IsNullOrEmpty(settings.Password);
        settings.Password = null;
        return (settings, hasPassword);
    }

    /// <param name="edited">Si la contraseña viene vacía se conserva la guardada.</param>
    public async Task<IReadOnlyList<ValidationError>> SaveAsync(NotificationSettings edited, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        edited.TenantId = user.TenantId;

        var errors = Validate(edited);
        if (errors.Count > 0)
        {
            return errors;
        }

        await repository.SaveAsync(await WithStoredPasswordAsync(edited, cancellationToken), cancellationToken);
        return [];
    }

    /// <summary>Envía un correo de prueba con la configuración que se está editando (sin guardarla).</summary>
    public async Task<ConnectionTestResult> SendTestAsync(NotificationSettings edited, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        edited.TenantId = user.TenantId;
        var errors = Validate(edited, requireEnabled: false);
        if (errors.Count > 0)
        {
            return new(false, errors[0].Message);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var settings = await WithStoredPasswordAsync(edited, timeout.Token);
            var smtp = smtpResolver.ForAlerts(settings) ?? throw new InvalidOperationException("No hay un servidor SMTP disponible.");
            await sender.SendAsync(smtp, EmailTemplates.Test(user.DisplayName) with { To = settings.RecipientList }, timeout.Token);
            return new(true, $"Correo de prueba enviado a {string.Join(", ", settings.RecipientList)} vía {smtp.Host}.");
        }
#pragma warning disable CA1031 // El error se muestra al usuario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new(false, ex is OperationCanceledException ? "Tiempo de espera agotado al conectar con el servidor SMTP." : ex.Message);
        }
    }

    private async Task<NotificationSettings> WithStoredPasswordAsync(NotificationSettings edited, CancellationToken cancellationToken)
    {
        if (!edited.UsePlatformSmtp && string.IsNullOrEmpty(edited.Password) && !string.IsNullOrWhiteSpace(edited.Username)
            && await repository.GetAsync(user.TenantId, cancellationToken) is { } stored)
        {
            edited.Password = stored.Password;
        }

        return edited;
    }

    private List<ValidationError> Validate(NotificationSettings settings, bool requireEnabled = true)
    {
        var errors = new List<ValidationError>();
        if (requireEnabled && !settings.Enabled)
        {
            return errors;
        }

        if (settings.UsePlatformSmtp)
        {
            if (smtpResolver.Platform is null)
            {
                errors.Add(new(nameof(settings.UsePlatformSmtp), "La plataforma no tiene un servidor SMTP configurado; usa uno propio."));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.SmtpHost))
            {
                errors.Add(new(nameof(settings.SmtpHost), "Indica el servidor SMTP."));
            }

            if (settings.SmtpPort is < 1 or > 65535)
            {
                errors.Add(new(nameof(settings.SmtpPort), "Puerto inválido."));
            }

            if (!IsEmail(settings.FromAddress))
            {
                errors.Add(new(nameof(settings.FromAddress), "Indica un correo remitente válido."));
            }
        }

        if (settings.RecipientList.Count == 0)
        {
            errors.Add(new(nameof(settings.Recipients), "Indica al menos un destinatario."));
        }
        else if (settings.RecipientList.FirstOrDefault(r => !IsEmail(r)) is { } invalid)
        {
            errors.Add(new(nameof(settings.Recipients), $"Correo inválido: {invalid}"));
        }

        return errors;
    }

    private static bool IsEmail(string? value) => MailAddress.TryCreate(value, out _);
}
