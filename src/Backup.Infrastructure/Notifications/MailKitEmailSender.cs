using Backup.Application.Abstractions;
using Backup.Domain.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Backup.Infrastructure.Notifications;

/// <summary>Envío SMTP con MailKit (STARTTLS, TLS implícito o sin cifrado).</summary>
public sealed class MailKitEmailSender : IEmailSender
{
    public async Task SendAsync(SmtpServer smtp, EmailMessage message, CancellationToken cancellationToken)
    {
        if (message.To.Count == 0)
        {
            throw new InvalidOperationException("No hay destinatarios configurados.");
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        foreach (var recipient in message.To)
        {
            mime.To.Add(MailboxAddress.Parse(recipient));
        }

        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        var options = smtp.Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };

        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, options, cancellationToken);
        }
        catch (SslHandshakeException ex)
        {
            throw new InvalidOperationException($"Falló la negociación TLS con {smtp.Host}:{smtp.Port}. Revisa el tipo de seguridad (587 = STARTTLS, 465 = SSL). {ex.Message}", ex);
        }

        if (!string.IsNullOrWhiteSpace(smtp.Username))
        {
            try
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, cancellationToken);
            }
            catch (AuthenticationException ex)
            {
                throw new InvalidOperationException($"El servidor SMTP rechazó el usuario o la contraseña. {ex.Message}", ex);
            }
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
