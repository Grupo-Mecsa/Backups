using Backup.Application.Abstractions;
using Backup.Domain.Runs;
using Microsoft.Extensions.Logging;

namespace Backup.Application.WhatsApp;

/// <summary>Envía el resultado de cada ejecución por WhatsApp a los destinatarios configurados en el tenant.</summary>
public sealed partial class WhatsAppRunNotifier(
    IWhatsAppSender sender,
    INotificationSettingsRepository settingsRepository,
    ITenantRepository tenants,
    ILogger<WhatsAppRunNotifier> logger) : IRunNotifier
{
    public async Task NotifyAsync(BackupRun run, CancellationToken cancellationToken)
    {
        if (run.Status == RunStatus.Running)
        {
            return;
        }

        var settings = await settingsRepository.GetAsync(run.TenantId, cancellationToken);
        if (settings is not { WhatsAppEnabled: true } || !(run.Status == RunStatus.Succeeded ? settings.WhatsAppOnSuccess : settings.WhatsAppOnFailure))
        {
            return;
        }

        if (WhatsAppConnection.For(settings, sender.Platform) is not { } connection)
        {
            return;
        }

        var targets = settings.WhatsAppRecipientList.Select(WhatsAppRecipients.ToChatId).OfType<string>().Distinct().ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var tenantName = (await tenants.GetAsync(run.TenantId, cancellationToken))?.Name ?? string.Empty;
        var text = WhatsAppMessages.RunFinished(run, tenantName);

        // Igual que el correo y Telegram: no retrasa el cierre de la ejecución.
        _ = Task.Run(async () =>
        {
            foreach (var chatId in targets)
            {
                try
                {
                    await sender.SendTextAsync(connection, chatId, text, CancellationToken.None);
                }
#pragma warning disable CA1031 // Un fallo de WhatsApp solo se registra
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogSendFailed(ex, run.JobName, chatId);
                }
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo enviar la alerta de WhatsApp del trabajo {Job} a {ChatId}")]
    private partial void LogSendFailed(Exception ex, string job, string chatId);
}
