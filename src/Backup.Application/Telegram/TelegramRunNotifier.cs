using Backup.Application.Abstractions;
using Backup.Domain.Runs;
using Microsoft.Extensions.Logging;

namespace Backup.Application.Telegram;

/// <summary>Envía el resultado de cada ejecución a los chats de Telegram suscritos al tenant.</summary>
public sealed partial class TelegramRunNotifier(
    ITelegramBot bot,
    ITelegramSubscriptionRepository subscriptions,
    ITenantRepository tenants,
    TelegramBotCommands commands,
    ILogger<TelegramRunNotifier> logger) : IRunNotifier
{
    public async Task NotifyAsync(BackupRun run, CancellationToken cancellationToken)
    {
        if (!bot.IsConfigured || run.Status == RunStatus.Running)
        {
            return;
        }

        var targets = (await subscriptions.ListByTenantAsync(run.TenantId, cancellationToken))
            .Where(s => run.Status == RunStatus.Succeeded ? s.NotifyOnSuccess : s.NotifyOnFailure)
            .Select(s => s.ChatId)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var tenantName = (await tenants.GetAsync(run.TenantId, cancellationToken))?.Name ?? string.Empty;
        var text = TelegramMessages.RunFinished(run, tenantName);

        // Igual que el correo: no retrasa el cierre de la ejecución.
        _ = Task.Run(async () =>
        {
            foreach (var chatId in targets)
            {
                try
                {
                    await bot.SendAsync(chatId, text, CancellationToken.None);
                }
                catch (TelegramChatUnavailableException ex)
                {
                    LogChatGone(chatId, ex.Message);
                    await commands.ForgetChatAsync(chatId, CancellationToken.None);
                }
#pragma warning disable CA1031 // Un fallo de Telegram solo se registra
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogSendFailed(ex, run.JobName, chatId);
                }
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo enviar la alerta de Telegram del trabajo {Job} al chat {ChatId}")]
    private partial void LogSendFailed(Exception ex, string job, long chatId);

    [LoggerMessage(Level = LogLevel.Information, Message = "El chat de Telegram {ChatId} ya no acepta mensajes ({Reason}); se elimina su suscripción")]
    private partial void LogChatGone(long chatId, string reason);
}
