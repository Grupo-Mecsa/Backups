using System.Buffers.Text;
using System.Text;
using Backup.Application.Abstractions;
using Backup.Application.Notifications;
using Backup.Application.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.Infrastructure.Identity;

/// <summary>Invitaciones y recuperación de contraseña por correo con tokens de un solo uso de Identity.</summary>
public sealed partial class PasswordRecovery(
    UserManager<ApplicationUser> users,
    SmtpResolver smtpResolver,
    IEmailSender sender,
    ITenantRepository tenants,
    IAccountLinks links,
    IOptions<DataProtectionTokenProviderOptions> tokenOptions,
    ILogger<PasswordRecovery> logger)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> RecentRequests = new(StringComparer.Ordinal);

    private TimeSpan TokenLifespan => tokenOptions.Value.TokenLifespan;

    public async Task<bool> CanSendAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await smtpResolver.ForAccountAsync(tenantId, cancellationToken) is not null;

    /// <summary>Correo para que un usuario nuevo defina su contraseña.</summary>
    public async Task SendInvitationAsync(ApplicationUser user, string? invitedBy, CancellationToken cancellationToken = default)
    {
        var smtp = await smtpResolver.ForAccountAsync(user.TenantId, cancellationToken) ?? throw NoSmtp();
        var tenantName = (await tenants.GetAsync(user.TenantId, cancellationToken))?.Name ?? "BackupHub";
        var link = links.SetPassword(user.Id, await CreateTokenAsync(user), invitation: true);
        await sender.SendAsync(smtp, EmailTemplates.Invitation(user.Email!, tenantName, invitedBy, link, TokenLifespan), cancellationToken);
    }

    public async Task SendResetAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        var smtp = await smtpResolver.ForAccountAsync(user.TenantId, cancellationToken) ?? throw NoSmtp();
        var link = links.SetPassword(user.Id, await CreateTokenAsync(user), invitation: false);
        await sender.SendAsync(smtp, EmailTemplates.PasswordReset(user.Email!, link, TokenLifespan), cancellationToken);
    }

    /// <summary>
    /// "Olvidé mi contraseña": nunca revela si el correo existe. Los fallos solo se registran.
    /// </summary>
    public async Task RequestResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || await users.IsLockedOutAsync(user))
        {
            return;
        }

        // Como mucho un correo cada 2 minutos por cuenta, para que no se pueda saturar el buzón de nadie.
        var now = DateTimeOffset.UtcNow;
        if (RecentRequests.TryGetValue(user.Id, out var last) && now - last < TimeSpan.FromMinutes(2))
        {
            return;
        }

        RecentRequests[user.Id] = now;

        try
        {
            await SendResetAsync(user, cancellationToken);
        }
#pragma warning disable CA1031 // No se informa al solicitante (evita enumerar cuentas)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogResetFailed(ex, email);
        }
    }

    public async Task<UserOperationResult> ResetAsync(string userId, string code, string newPassword)
    {
        var user = await users.FindByIdAsync(userId);
        string token;
        try
        {
            token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(code));
        }
        catch (FormatException)
        {
            user = null;
            token = string.Empty;
        }

        if (user is null)
        {
            return UserOperationResult.Fail("El enlace no es válido.");
        }

        var result = await users.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
                ? UserOperationResult.Fail("El enlace caducó o ya se usó. Pide uno nuevo.")
                : new UserOperationResult(false, [.. result.Errors.Select(IdentityMessages.Translate)]);
        }

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
        }

        return UserOperationResult.Ok;
    }

    private async Task<string> CreateTokenAsync(ApplicationUser user) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));

    private static InvalidOperationException NoSmtp() =>
        new("No hay un servidor SMTP disponible: configura el SMTP de la plataforma (Smtp__*) o el de Alertas por correo.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo enviar el correo de recuperación a {Email}")]
    private partial void LogResetFailed(Exception ex, string email);
}
