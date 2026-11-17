using System.Net.Mail;
using Backup.Application.Abstractions;
using Backup.Application.Jobs;
using Backup.Application.Security;
using Backup.Application.WhatsApp;
using Backup.Domain.Notifications;

namespace Backup.Application.Notifications;

/// <summary>Configuración de alertas por correo del tenant actual (solo administradores).</summary>
public sealed class NotificationService(
    INotificationSettingsRepository repository,
    SmtpResolver smtpResolver,
    IEmailSender sender,
    IWhatsAppSender whatsApp,
    ITenantRepository tenants,
    ICurrentUser user)
{
    /// <summary>URL de la instancia de WAHA de la plataforma, o null si el operador no la configuró.</summary>
    public string? PlatformWhatsApp => whatsApp.Platform is { } platform ? $"{platform.BaseUrl} · sesión {platform.Session}" : null;

    /// <summary>Remitente del SMTP de plataforma, o <c>null</c> si el operador no lo configuró.</summary>
    public string? PlatformSender => smtpResolver.PlatformSender;

    /// <summary>
    /// Configuración sin secretos: la contraseña SMTP y la API key de WAHA nunca vuelven al navegador;
    /// <c>HasPassword</c> y <c>HasWhatsAppKey</c> indican si hay una guardada.
    /// </summary>
    public async Task<(NotificationSettings Settings, bool HasPassword, bool HasWhatsAppKey)> GetAsync(CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var settings = await repository.GetAsync(user.TenantId, cancellationToken)
            ?? new NotificationSettings { TenantId = user.TenantId, UsePlatformSmtp = smtpResolver.Platform is not null, UsePlatformWhatsApp = whatsApp.Platform is not null };
        var hasPassword = !string.IsNullOrEmpty(settings.Password);
        var hasKey = !string.IsNullOrEmpty(settings.WahaApiKey);
        settings.Password = null;
        settings.WahaApiKey = null;
        return (settings, hasPassword, hasKey);
    }

    /// <param name="edited">Si la contraseña viene vacía se conserva la guardada.</param>
    public async Task<IReadOnlyList<ValidationError>> SaveAsync(NotificationSettings edited, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        edited.TenantId = user.TenantId;

        var errors = Validate(edited);
        errors.AddRange(ValidateWhatsApp(edited));
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

    /// <summary>Prueba la conexión con WAHA que se está editando: instancia accesible, API key válida y sesión lista.</summary>
    public async Task<ConnectionTestResult> TestWhatsAppAsync(NotificationSettings edited, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var errors = ValidateWhatsAppConnection(edited);
        if (errors.Count > 0)
        {
            return new(false, errors[0].Message);
        }

        try
        {
            var connection = await ConnectionForAsync(edited, cancellationToken);
            var sessions = await WithTimeout(t => whatsApp.ListSessionsAsync(connection, t), 15, cancellationToken);
            if (sessions.FirstOrDefault(s => s.Name == connection.Session) is not { } session)
            {
                return new(false, sessions.Count == 0
                    ? "WAHA respondió, pero no tiene sesiones. Crea una y vincula WhatsApp desde su panel."
                    : $"WAHA no tiene la sesión «{connection.Session}». Disponibles: {string.Join(", ", sessions.Select(s => s.Name))}.");
            }

            return session.IsWorking
                ? new(true, $"Conectado a WAHA. Sesión «{session.Name}» lista{(session.Phone is null ? string.Empty : $", enviará desde {session.Phone}")}.")
                : new(false, $"La sesión «{session.Name}» está en estado {session.Status}: vincúlala o iníciala desde el panel de WAHA.");
        }
#pragma warning disable CA1031 // El error se muestra al usuario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new(false, Describe(ex));
        }
    }

    /// <summary>Sesiones de la instancia de WAHA que se está editando, para elegir desde cuál enviar.</summary>
    public async Task<IReadOnlyList<WhatsAppSession>> ListWhatsAppSessionsAsync(NotificationSettings edited, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var probe = await ConnectionForAsync(edited, cancellationToken, requireSession: false);
        return await WithTimeout(t => whatsApp.ListSessionsAsync(probe, t), 15, cancellationToken);
    }

    /// <summary>Grupos de WhatsApp de la sesión, para elegirlos como destinatarios.</summary>
    public async Task<IReadOnlyList<WhatsAppChat>> ListWhatsAppGroupsAsync(NotificationSettings edited, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var connection = await ConnectionForAsync(edited, cancellationToken);
        var groups = await WithTimeout(t => whatsApp.ListGroupsAsync(connection, t), 20, cancellationToken);
        return [.. groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Envía un WhatsApp de prueba a los destinatarios que se están editando (sin guardarlos).</summary>
    public async Task<ConnectionTestResult> SendWhatsAppTestAsync(NotificationSettings edited, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var errors = ValidateWhatsApp(edited, requireEnabled: false);
        if (errors.Count > 0)
        {
            return new(false, errors[0].Message);
        }

        WhatsAppConnection connection;
        try
        {
            connection = await ConnectionForAsync(edited, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, ex.Message);
        }

        var tenantName = (await tenants.GetAsync(user.TenantId, cancellationToken))?.Name ?? string.Empty;
        var text = WhatsAppMessages.Test(user.DisplayName ?? "un administrador", tenantName);
        var failed = new List<string>();
        foreach (var recipient in edited.WhatsAppRecipientList)
        {
            try
            {
                await WithTimeout(async t => { await whatsApp.SendTextAsync(connection, WhatsAppRecipients.ToChatId(recipient)!, text, t); return true; }, 20, cancellationToken);
            }
#pragma warning disable CA1031 // El error se muestra al usuario
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failed.Add($"{recipient}: {Describe(ex)}");
            }
        }

        var sent = edited.WhatsAppRecipientList.Count - failed.Count;
        return failed.Count == 0
            ? new(true, $"Mensaje de prueba enviado a {sent} destinatario(s) por WhatsApp.")
            : new(false, $"Enviado a {sent} de {edited.WhatsAppRecipientList.Count}. Falló: {string.Join(" · ", failed)}");
    }

    /// <summary>Conexión con lo que hay en pantalla; si la API key viene vacía, usa la guardada.</summary>
    private async Task<WhatsAppConnection> ConnectionForAsync(NotificationSettings edited, CancellationToken cancellationToken, bool requireSession = true)
    {
        if (edited.UsePlatformWhatsApp)
        {
            return whatsApp.Platform ?? throw new InvalidOperationException("La plataforma no tiene WhatsApp configurado; usa una instancia propia.");
        }

        if (string.IsNullOrWhiteSpace(edited.WahaUrl))
        {
            throw new InvalidOperationException("Indica la URL de WAHA.");
        }

        if (requireSession && string.IsNullOrWhiteSpace(edited.WahaSession))
        {
            throw new InvalidOperationException("Indica la sesión de WAHA.");
        }

        var key = edited.WahaApiKey;
        if (string.IsNullOrEmpty(key) && await repository.GetAsync(user.TenantId, cancellationToken) is { } stored)
        {
            key = stored.WahaApiKey;
        }

        return new(edited.WahaUrl.Trim(), key, edited.WahaSession?.Trim() ?? "default");
    }

    private static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> action, int seconds, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await action(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("WAHA no respondió a tiempo: revisa la URL y que el servidor sea accesible desde BackupHub.");
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError } =>
            "No se pudo conectar con WAHA: revisa la URL y que el servidor sea accesible desde BackupHub.",
        _ => ex.Message,
    };

    private List<ValidationError> ValidateWhatsAppConnection(NotificationSettings settings)
    {
        var errors = new List<ValidationError>();
        if (settings.UsePlatformWhatsApp)
        {
            if (whatsApp.Platform is null)
            {
                errors.Add(new(nameof(settings.UsePlatformWhatsApp), "La plataforma no tiene WhatsApp configurado; usa una instancia propia."));
            }

            return errors;
        }

        if (!Uri.TryCreate(settings.WahaUrl?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            errors.Add(new(nameof(settings.WahaUrl), "Indica la URL de WAHA (p. ej. http://192.168.1.3:3000)."));
        }

        if (string.IsNullOrWhiteSpace(settings.WahaSession))
        {
            errors.Add(new(nameof(settings.WahaSession), "Indica la sesión de WAHA (p. ej. default)."));
        }

        return errors;
    }

    private List<ValidationError> ValidateWhatsApp(NotificationSettings settings, bool requireEnabled = true)
    {
        var errors = new List<ValidationError>();
        if (requireEnabled && !settings.WhatsAppEnabled)
        {
            return errors;
        }

        errors.AddRange(ValidateWhatsAppConnection(settings));
        if (settings.WhatsAppRecipientList.Count == 0)
        {
            errors.Add(new(nameof(settings.WhatsAppRecipients), "Indica al menos un número o grupo."));
        }
        else if (settings.WhatsAppRecipientList.FirstOrDefault(r => WhatsAppRecipients.ToChatId(r) is null) is { } invalid)
        {
            errors.Add(new(nameof(settings.WhatsAppRecipients), $"«{invalid}» no es un número con código de país (p. ej. +506 7070 7070) ni un ID de grupo."));
        }

        return errors;
    }

    private async Task<NotificationSettings> WithStoredPasswordAsync(NotificationSettings edited, CancellationToken cancellationToken)
    {
        // La API key de WAHA vacía significa "sin cambios" (salvo que se vacíe la URL: entonces se descarta).
        if (string.IsNullOrEmpty(edited.WahaApiKey) && !string.IsNullOrWhiteSpace(edited.WahaUrl)
            && await repository.GetAsync(user.TenantId, cancellationToken) is { WahaApiKey: { Length: > 0 } storedKey })
        {
            edited.WahaApiKey = storedKey;
        }

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
