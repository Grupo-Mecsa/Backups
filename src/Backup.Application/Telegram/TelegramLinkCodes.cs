using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Backup.Application.Telegram;

/// <summary>
/// Códigos de un solo uso para vincular un chat: la UI genera el código y el usuario lo envía al bot
/// con <c>/start CODIGO</c> (el enlace <c>t.me/bot?start=CODIGO</c> lo hace automáticamente).
/// Viven en memoria: un reinicio solo obliga a generar otro.
/// </summary>
public sealed class TelegramLinkCodes(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (Guid TenantId, string UserId, DateTimeOffset Expires)> _codes = new(StringComparer.Ordinal);

    public string Create(Guid tenantId, string userId)
    {
        Purge();
        // Solo [A-Za-z0-9_-] y <= 64 caracteres: requisito del parámetro start de Telegram.
        var code = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", 16);
        _codes[code] = (tenantId, userId, timeProvider.GetUtcNow() + Lifetime);
        return code;
    }

    public bool TryConsume(string code, out Guid tenantId, out string userId)
    {
        if (_codes.TryRemove(code, out var entry) && entry.Expires > timeProvider.GetUtcNow())
        {
            (tenantId, userId) = (entry.TenantId, entry.UserId);
            return true;
        }

        (tenantId, userId) = (Guid.Empty, string.Empty);
        return false;
    }

    private void Purge()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (code, entry) in _codes)
        {
            if (entry.Expires <= now)
            {
                _codes.TryRemove(code, out _);
            }
        }
    }
}
