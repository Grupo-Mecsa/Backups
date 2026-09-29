using System.Collections.Concurrent;
using Backup.Application.Abstractions;
using Backup.Application.Security;
using Backup.Application.Telegram;
using Backup.Domain.Notifications;

namespace Backup.Tests;

/// <summary>Captura los correos en lugar de enviarlos.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<(SmtpServer Smtp, EmailMessage Message)> Sent { get; } = new();

    public Task SendAsync(SmtpServer smtp, EmailMessage message, CancellationToken cancellationToken)
    {
        Sent.Enqueue((smtp, message));
        return Task.CompletedTask;
    }
}

/// <summary>Bot de Telegram en memoria; <see cref="BlockedChats"/> simula chats que bloquearon al bot.</summary>
public sealed class FakeTelegramBot : ITelegramBot
{
    public ConcurrentQueue<(long ChatId, string Html)> Sent { get; } = new();
    public HashSet<long> BlockedChats { get; } = [];

    public bool IsConfigured => true;
    public string? Username => "backuphub_test_bot";

    public Task SendAsync(long chatId, string html, CancellationToken cancellationToken)
    {
        if (BlockedChats.Contains(chatId))
        {
            throw new TelegramChatUnavailableException("Forbidden: bot was blocked by the user");
        }

        Sent.Enqueue((chatId, html));
        return Task.CompletedTask;
    }
}

public sealed class TestLinks : IAccountLinks
{
    public string SetPassword(string userId, string token, bool invitation) =>
        $"https://backup.test/account/reset-password?user={Uri.EscapeDataString(userId)}&code={Uri.EscapeDataString(token)}{(invitation ? "&invite=1" : null)}";

    public string Absolute(string path) => "https://backup.test/" + path.TrimStart('/');
}

public static class Eventually
{
    /// <summary>Espera a que se cumpla una condición producida en segundo plano (los notificadores usan Task.Run).</summary>
    public static async Task TrueAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("La condición no se cumplió a tiempo.");
            }

            await Task.Delay(25);
        }
    }
}
