using Backup.Application.Abstractions;
using Backup.Domain.Runs;
using Microsoft.Extensions.Logging;

namespace Backup.Application.Notifications;

/// <summary>Envía una alerta por correo al terminar una ejecución, según la configuración del tenant.</summary>
public sealed partial class EmailRunNotifier(
    INotificationSettingsRepository settingsRepository,
    SmtpResolver smtpResolver,
    IEmailSender sender,
    ILogger<EmailRunNotifier> logger) : IRunNotifier
{
    public async Task NotifyAsync(BackupRun run, CancellationToken cancellationToken)
    {
        if (run.Status == RunStatus.Running)
        {
            return;
        }

        var settings = await settingsRepository.GetAsync(run.TenantId, cancellationToken);
        if (settings is not { Enabled: true } || settings.RecipientList.Count == 0 || smtpResolver.ForAlerts(settings) is not { } smtp)
        {
            return;
        }

        var wanted = run.Status == RunStatus.Succeeded ? settings.NotifyOnSuccess : settings.NotifyOnFailure;
        if (!wanted)
        {
            return;
        }

        // El envío no debe retrasar el cierre de la ejecución.
        _ = Task.Run(async () =>
        {
            try
            {
                await sender.SendAsync(smtp, EmailTemplates.RunFinished(run) with { To = settings.RecipientList }, CancellationToken.None);
            }
#pragma warning disable CA1031 // Un fallo de SMTP solo se registra
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSendFailed(ex, run.JobName);
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo enviar la alerta por correo del trabajo {Job}")]
    private partial void LogSendFailed(Exception ex, string job);
}
