using Backup.Application.Abstractions;
using Backup.Application.Jobs;
using Backup.Application.Security;
using Backup.Domain.Notifications;

namespace Backup.Application.Telegram;

/// <param name="Code">Código de un solo uso (también sirve enviando <c>/start CODIGO</c> al bot).</param>
/// <param name="PrivateUrl">Abre el chat privado con el bot.</param>
/// <param name="GroupUrl">Permite elegir un grupo al que agregar el bot.</param>
public sealed record TelegramLink(string Code, string? PrivateUrl, string? GroupUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// Suscripciones de Telegram. Cualquier usuario vincula y gestiona sus propios chats;
/// un administrador además ve y elimina los de todo su tenant.
/// </summary>
public sealed class TelegramService(
    ITelegramBot bot,
    TelegramLinkCodes codes,
    TelegramBotCommands commands,
    ITelegramSubscriptionRepository repository,
    ITenantRepository tenants,
    ICurrentUser user,
    TimeProvider timeProvider)
{
    public bool IsAvailable => bot.IsConfigured;

    public string? BotUsername => bot.Username;

    public TelegramLink CreateLink()
    {
        user.EnsureAuthenticated();
        EnsureAvailable();
        var code = codes.Create(user.TenantId, user.UserId!);
        var baseUrl = bot.Username is { } name ? $"https://t.me/{name}" : null;
        return new TelegramLink(
            code,
            baseUrl is null ? null : $"{baseUrl}?start={code}",
            baseUrl is null ? null : $"{baseUrl}?startgroup={code}",
            timeProvider.GetUtcNow() + TelegramLinkCodes.Lifetime);
    }

    /// <summary>Chats del usuario en el tenant actual.</summary>
    public async Task<IReadOnlyList<TelegramSubscription>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        user.EnsureAuthenticated();
        return [.. (await repository.ListByTenantAsync(user.TenantId, cancellationToken)).Where(s => s.UserId == user.UserId)];
    }

    public async Task<IReadOnlyList<TelegramSubscription>> ListTenantAsync(CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        return await repository.ListByTenantAsync(user.TenantId, cancellationToken);
    }

    public async Task UpdatePreferencesAsync(Guid id, bool onFailure, bool onSuccess, CancellationToken cancellationToken = default)
    {
        var subscription = await GetEditableAsync(id, cancellationToken);
        subscription.NotifyOnFailure = onFailure;
        subscription.NotifyOnSuccess = onSuccess;
        await repository.UpdateAsync(subscription, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await GetEditableAsync(id, cancellationToken);
        await repository.DeleteAsync(subscription.Id, cancellationToken);
        commands.NotifyChanged(subscription.TenantId);
    }

    public async Task<ConnectionTestResult> SendTestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await GetEditableAsync(id, cancellationToken);
        EnsureAvailable();
        try
        {
            var tenantName = (await tenants.GetAsync(subscription.TenantId, cancellationToken))?.Name ?? string.Empty;
            await bot.SendAsync(subscription.ChatId, TelegramMessages.Test(tenantName), cancellationToken);
            return new(true, $"Mensaje enviado a {subscription.ChatTitle}.");
        }
        catch (TelegramChatUnavailableException ex)
        {
            await commands.ForgetChatAsync(subscription.ChatId, cancellationToken);
            return new(false, $"El chat ya no acepta mensajes del bot y se desvinculó: {ex.Message}");
        }
#pragma warning disable CA1031 // El error se muestra al usuario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new(false, ex.Message);
        }
    }

    /// <summary>Suscripción del tenant actual que el usuario puede modificar (la suya, o cualquiera si administra).</summary>
    private async Task<TelegramSubscription> GetEditableAsync(Guid id, CancellationToken cancellationToken)
    {
        user.EnsureAuthenticated();
        var subscription = await repository.GetAsync(id, cancellationToken);
        if (subscription is null || !user.Owns(subscription.TenantId))
        {
            throw new KeyNotFoundException("Suscripción no encontrada.");
        }

        if (subscription.UserId != user.UserId && !user.CanManage)
        {
            throw new UnauthorizedAccessException("Solo puedes modificar tus propios chats.");
        }

        return subscription;
    }

    private void EnsureAvailable()
    {
        if (!bot.IsConfigured)
        {
            throw new InvalidOperationException("No hay un bot de Telegram configurado (Telegram__BotToken).");
        }
    }
}
