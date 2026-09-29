using Backup.Application.Abstractions;
using Backup.Domain.Notifications;

namespace Backup.Application.Telegram;

/// <summary>Interpreta los comandos que recibe el bot y devuelve la respuesta (null = no responder).</summary>
public sealed class TelegramBotCommands(
    TelegramLinkCodes codes,
    ITelegramSubscriptionRepository subscriptions,
    ITenantRepository tenants,
    TimeProvider timeProvider)
{
    /// <summary>Se vinculó o desvinculó un chat del tenant indicado (la UI se refresca en vivo).</summary>
    public event Action<Guid>? SubscriptionsChanged;

    public async Task<string?> HandleAsync(TelegramIncoming message, CancellationToken cancellationToken = default)
    {
        var parts = message.Text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].StartsWith('/'))
        {
            // En grupos se ignora la conversación normal; en privado se orienta al usuario.
            return message.IsGroup ? null : TelegramMessages.Help;
        }

        // En grupos los comandos llegan como /start@MiBot.
        var command = parts[0].Split('@')[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : null;

        return command switch
        {
            "/start" when argument is not null => await LinkAsync(message, argument, cancellationToken),
            "/start" or "/ayuda" or "/help" => TelegramMessages.Help,
            "/estado" or "/status" => await StatusAsync(message.ChatId, cancellationToken),
            "/stop" => await StopAsync(message.ChatId, cancellationToken),
            _ => message.IsGroup ? null : TelegramMessages.Help,
        };
    }

    /// <summary>El bot fue bloqueado o expulsado del chat: se olvidan sus suscripciones.</summary>
    public async Task ForgetChatAsync(long chatId, CancellationToken cancellationToken = default)
    {
        var affected = await subscriptions.ListByChatAsync(chatId, cancellationToken);
        if (affected.Count > 0)
        {
            await subscriptions.DeleteByChatAsync(chatId, cancellationToken);
            foreach (var tenantId in affected.Select(s => s.TenantId).Distinct())
            {
                SubscriptionsChanged?.Invoke(tenantId);
            }
        }
    }

    public void NotifyChanged(Guid tenantId) => SubscriptionsChanged?.Invoke(tenantId);

    private async Task<string> LinkAsync(TelegramIncoming message, string code, CancellationToken cancellationToken)
    {
        if (!codes.TryConsume(code, out var tenantId, out var userId) || await tenants.GetAsync(tenantId, cancellationToken) is not { } tenant)
        {
            return TelegramMessages.InvalidCode;
        }

        var saved = await subscriptions.UpsertAsync(new TelegramSubscription
        {
            TenantId = tenantId,
            UserId = userId,
            ChatId = message.ChatId,
            ChatTitle = message.ChatTitle,
            IsGroup = message.IsGroup,
            CreatedAt = timeProvider.GetUtcNow(),
        }, cancellationToken);

        SubscriptionsChanged?.Invoke(tenantId);
        return TelegramMessages.Linked(tenant.Name, saved.NotifyOnFailure, saved.NotifyOnSuccess);
    }

    private async Task<string> StatusAsync(long chatId, CancellationToken cancellationToken)
    {
        var list = new List<(string, bool, bool)>();
        foreach (var subscription in await subscriptions.ListByChatAsync(chatId, cancellationToken))
        {
            var name = (await tenants.GetAsync(subscription.TenantId, cancellationToken))?.Name ?? "—";
            list.Add((name, subscription.NotifyOnFailure, subscription.NotifyOnSuccess));
        }

        return TelegramMessages.Status(list);
    }

    private async Task<string> StopAsync(long chatId, CancellationToken cancellationToken)
    {
        var affected = await subscriptions.ListByChatAsync(chatId, cancellationToken);
        await ForgetChatAsync(chatId, cancellationToken);
        return TelegramMessages.Stopped(affected.Count);
    }
}
