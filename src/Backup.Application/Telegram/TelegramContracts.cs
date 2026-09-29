using Backup.Domain.Notifications;

namespace Backup.Application.Telegram;

public interface ITelegramSubscriptionRepository
{
    Task<IReadOnlyList<TelegramSubscription>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TelegramSubscription>> ListByChatAsync(long chatId, CancellationToken cancellationToken = default);
    Task<TelegramSubscription?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Crea la suscripción o, si el chat ya estaba vinculado al tenant, la actualiza.</summary>
    Task<TelegramSubscription> UpsertAsync(TelegramSubscription subscription, CancellationToken cancellationToken = default);

    Task UpdateAsync(TelegramSubscription subscription, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Elimina todas las suscripciones de un chat (comando /stop o bot expulsado).</summary>
    Task<int> DeleteByChatAsync(long chatId, CancellationToken cancellationToken = default);

    /// <summary>Elimina los chats vinculados por un usuario (al eliminar su cuenta).</summary>
    Task<int> DeleteByUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Cliente mínimo de la Bot API de Telegram.</summary>
public interface ITelegramBot
{
    /// <summary>Hay un token de bot configurado (<c>Telegram__BotToken</c>).</summary>
    bool IsConfigured { get; }

    /// <summary>@usuario del bot, conocido tras conectar con Telegram; null mientras no se sepa.</summary>
    string? Username { get; }

    /// <summary>Envía un mensaje con formato HTML de Telegram.</summary>
    /// <exception cref="TelegramChatUnavailableException">El chat bloqueó o expulsó al bot.</exception>
    Task SendAsync(long chatId, string html, CancellationToken cancellationToken);
}

/// <summary>El bot ya no puede escribir en el chat (bloqueado, expulsado o chat eliminado).</summary>
public sealed class TelegramChatUnavailableException(string message) : Exception(message);

/// <summary>Mensaje de texto recibido por el bot.</summary>
public sealed record TelegramIncoming(long ChatId, string ChatTitle, bool IsGroup, string Text);
