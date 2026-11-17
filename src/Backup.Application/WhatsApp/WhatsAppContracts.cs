using Backup.Domain.Notifications;

namespace Backup.Application.WhatsApp;

/// <summary>Un chat de WhatsApp al que se puede enviar (contacto o grupo).</summary>
public sealed record WhatsAppChat(string Id, string Name);

/// <summary>Una sesión de WAHA: nombre, estado (WORKING, SCAN_QR_CODE, STOPPED…) y número conectado.</summary>
public sealed record WhatsAppSession(string Name, string Status, string? Me)
{
    public bool IsWorking => Status == "WORKING";

    /// <summary>"+50670745530" a partir de "50670745530@c.us".</summary>
    public string? Phone => Me is null ? null : "+" + Me.Split('@')[0];
}

/// <summary>Instancia de WAHA y sesión desde la que se envía.</summary>
public sealed record WhatsAppConnection(string BaseUrl, string? ApiKey, string Session)
{
    /// <summary>
    /// La conexión que usa un tenant: la de la plataforma si eligió usarla, si no la suya (URL y sesión completas).
    /// Null si no hay ninguna utilizable.
    /// </summary>
    public static WhatsAppConnection? For(NotificationSettings settings, WhatsAppConnection? platform) =>
        settings.UsePlatformWhatsApp
            ? platform
            : !string.IsNullOrWhiteSpace(settings.WahaUrl) && !string.IsNullOrWhiteSpace(settings.WahaSession)
                ? new(settings.WahaUrl.Trim(), settings.WahaApiKey, settings.WahaSession.Trim())
                : null;
}

/// <summary>Envío de mensajes de WhatsApp (implementado con WAHA, WhatsApp HTTP API).</summary>
public interface IWhatsAppSender
{
    /// <summary>Instancia de la plataforma (variables <c>WhatsApp__*</c>), o null si el operador no la configuró.</summary>
    WhatsAppConnection? Platform { get; }

    /// <summary>Envía texto con el formato de WhatsApp (*negrita*, _cursiva_, ```monoespaciado```).</summary>
    Task SendTextAsync(WhatsAppConnection connection, string chatId, string text, CancellationToken cancellationToken);

    /// <summary>Grupos en los que está la sesión, para elegirlos como destinatarios.</summary>
    Task<IReadOnlyList<WhatsAppChat>> ListGroupsAsync(WhatsAppConnection connection, CancellationToken cancellationToken);

    /// <summary>Sesiones de la instancia (la sesión de <paramref name="connection"/> no se usa).</summary>
    Task<IReadOnlyList<WhatsAppSession>> ListSessionsAsync(WhatsAppConnection connection, CancellationToken cancellationToken);
}
