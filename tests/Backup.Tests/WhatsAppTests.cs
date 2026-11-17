using Backup.Application.WhatsApp;
using Backup.Domain.Runs;

namespace Backup.Tests;

public sealed class WhatsAppTests
{
    [Theory]
    [InlineData("+506 7074-5530", "50670745530@c.us")]
    [InlineData("50670745530", "50670745530@c.us")]
    [InlineData("120363412198097398@g.us", "120363412198097398@g.us")]
    [InlineData("Prueba Facturas <120363412198097398@g.us>", "120363412198097398@g.us")]
    [InlineData("7074", null)]
    [InlineData("soporte@empresa.com", null)]
    [InlineData("hola", null)]
    public void Recipients_become_chat_ids(string input, string? expected) =>
        Assert.Equal(expected, WhatsAppRecipients.ToChatId(input));

    [Fact]
    public void Group_format_round_trips()
    {
        var formatted = WhatsAppRecipients.Format(new WhatsAppChat("1203@g.us", "TI <Respaldos>, CR"));
        Assert.Equal("1203@g.us", WhatsAppRecipients.ToChatId(formatted));
    }

    [Fact]
    public void Failure_message_explains_the_error()
    {
        var run = new BackupRun
        {
            JobName = "Sealmec",
            Status = RunStatus.Failed,
            StartedAt = DateTimeOffset.UnixEpoch,
            Error = "No se pudo abrir el recurso 'Backup': el recurso compartido no existe en el servidor (STATUS_BAD_NETWORK_NAME)",
        };

        var text = WhatsAppMessages.RunFinished(run, "Principal");

        Assert.Contains("❌ *Respaldo falló*", text);
        Assert.Contains("*La carpeta compartida «Backup» no existe en el servidor*", text);
        Assert.Contains("💡", text);
    }
}
