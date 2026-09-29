using Backup.Application.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backup.Infrastructure.Telegram;

/// <summary>
/// Recibe los mensajes del bot con long polling (getUpdates): no requiere URL pública ni HTTPS entrante.
/// No hace nada si no hay token configurado.
/// </summary>
public sealed partial class TelegramPollingService(
    TelegramBotClient bot,
    TelegramBotCommands commands,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    private const int PollSeconds = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!bot.IsConfigured)
        {
            return;
        }

        var offset = 0L;
        var backoff = TimeSpan.FromSeconds(5);
        var connected = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!connected)
                {
                    LogConnected(await bot.GetMeAsync(stoppingToken));
                    connected = true;
                }

                foreach (var update in await bot.GetUpdatesAsync(offset, PollSeconds, stoppingToken))
                {
                    offset = update.UpdateId + 1;
                    await HandleAsync(update, stoppingToken);
                }

                backoff = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (TelegramApiException ex) when (ex.ErrorCode == 401)
            {
                // Token inválido: reintentar seguido no sirve de nada.
                LogInvalidToken();
                await DelayAsync(TimeSpan.FromMinutes(10), stoppingToken);
            }
            catch (TelegramApiException ex) when (ex.ErrorCode == 409)
            {
                LogConflict(ex.Message);
                await DelayAsync(TimeSpan.FromMinutes(1), stoppingToken);
            }
#pragma warning disable CA1031 // El servicio debe seguir vivo ante fallos de red o de Telegram
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPollFailed(ex, backoff.TotalSeconds);
                await DelayAsync(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));
            }
        }
    }

    private async Task HandleAsync(TgUpdate update, CancellationToken cancellationToken)
    {
        // El bot salió o fue expulsado de un grupo, o el usuario lo bloqueó.
        if (update.MyChatMember is { NewChatMember.Status: "kicked" or "left" } member)
        {
            await commands.ForgetChatAsync(member.Chat.Id, cancellationToken);
            return;
        }

        if (update.Message is not { Text: { Length: > 0 } text } message)
        {
            return;
        }

        var chat = message.Chat;
        var isGroup = chat.Type is "group" or "supergroup";
        var title = isGroup
            ? chat.Title ?? $"Grupo {chat.Id}"
            : chat.Username is { } username ? "@" + username : string.Join(' ', new[] { chat.FirstName, chat.LastName }.Where(n => !string.IsNullOrEmpty(n)));

        try
        {
            var reply = await commands.HandleAsync(new TelegramIncoming(chat.Id, title, isGroup, text), cancellationToken);
            if (reply is not null)
            {
                await bot.SendAsync(chat.Id, reply, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Un mensaje problemático no debe detener el bot
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHandleFailed(ex, chat.Id);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bot de Telegram conectado: @{Username}")]
    private partial void LogConnected(string username);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telegram rechazó el token del bot (Telegram__BotToken). Revísalo con @BotFather; se reintentará en 10 minutos.")]
    private partial void LogInvalidToken();

    [LoggerMessage(Level = LogLevel.Error, Message = "Telegram rechazó getUpdates ({Reason}). ¿El bot tiene un webhook o hay otra instancia usando el mismo token?")]
    private partial void LogConflict(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error consultando Telegram; reintento en {Seconds} s")]
    private partial void LogPollFailed(Exception ex, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo procesar el mensaje del chat {ChatId}")]
    private partial void LogHandleFailed(Exception ex, long chatId);
}
