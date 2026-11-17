using Backup.Application.WhatsApp;
using Backup.Domain.Notifications;

namespace Backup.Tests;

public sealed class WhatsAppConnectionTests
{
    private static readonly WhatsAppConnection Platform = new("http://waha:3000", "k", "default");

    [Fact]
    public void Own_instance_is_used_when_complete()
    {
        var connection = WhatsAppConnection.For(new NotificationSettings { WahaUrl = " http://192.168.1.3:3000 ", WahaApiKey = "x", WahaSession = "demo" }, Platform);
        Assert.Equal(new WhatsAppConnection("http://192.168.1.3:3000", "x", "demo"), connection);
    }

    [Fact]
    public void Platform_is_used_only_when_chosen()
    {
        Assert.Same(Platform, WhatsAppConnection.For(new NotificationSettings { UsePlatformWhatsApp = true }, Platform));
        Assert.Null(WhatsAppConnection.For(new NotificationSettings(), Platform));
        Assert.Null(WhatsAppConnection.For(new NotificationSettings { UsePlatformWhatsApp = true }, null));
    }
}
