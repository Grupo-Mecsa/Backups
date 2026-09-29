using System.Net.Mail;
using Backup.Application.Abstractions;
using Backup.Application.Notifications;
using Backup.Application.Security;
using Backup.Domain.Tenants;
using Backup.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.Infrastructure.Identity;

/// <summary>
/// Autorregistro. El primer usuario de una organización queda aprobado automáticamente como su administrador;
/// quien pide unirse a una organización existente espera la aprobación de sus administradores.
/// Aprobar o rechazar tenants pendientes se mantiene para registros hechos con versiones anteriores.
/// </summary>
public sealed partial class SelfRegistration(
    UserManager<ApplicationUser> users,
    IDbContextFactory<BackupDbContext> contextFactory,
    IOptionsMonitor<RegistrationOptions> options,
    SmtpResolver smtpResolver,
    IEmailSender sender,
    IAccountLinks links,
    ICurrentUser current,
    TimeProvider timeProvider,
    ILogger<SelfRegistration> logger)
{
    public RegistrationMode Mode => options.CurrentValue.Mode;

    /// <summary>
    /// Registra al usuario. Si la organización no existe, la crea y el usuario queda aprobado como su administrador
    /// (es su primer usuario). Si ya existe, queda como lector pendiente hasta que un administrador de ella lo apruebe.
    /// </summary>
    /// <returns>El usuario creado y si ya puede iniciar sesión, o los errores.</returns>
    public async Task<(UserOperationResult Result, ApplicationUser? User, bool CanSignIn)> RegisterAsync(
        string organization, string displayName, string email, string password, CancellationToken cancellationToken = default)
    {
        if (Mode == RegistrationMode.Disabled)
        {
            return (UserOperationResult.Fail("El registro está deshabilitado. Pide a un administrador que te cree una cuenta."), null, false);
        }

        organization = organization.Trim();
        email = email.Trim();
        var errors = new List<string>();
        if (organization.Length is 0 or > 100)
        {
            errors.Add("Indica el nombre de tu organización (máx. 100 caracteres).");
        }

        if (!MailAddress.TryCreate(email, out _))
        {
            errors.Add("Indica un correo válido.");
        }
        else if (await users.FindByEmailAsync(email) is not null)
        {
            errors.Add("Ya existe una cuenta con ese correo. Inicia sesión o recupera tu contraseña.");
        }

        // Valida la contraseña antes de crear nada, para no dejar tenants huérfanos.
        var candidate = new ApplicationUser { UserName = email, Email = email };
        foreach (var validator in users.PasswordValidators)
        {
            var check = await validator.ValidateAsync(users, candidate, password);
            errors.AddRange(check.Errors.Select(IdentityMessages.Translate));
        }

        if (errors.Count > 0)
        {
            return (new UserOperationResult(false, [.. errors.Distinct()]), null, false);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = (await db.Tenants.ToListAsync(cancellationToken))
            .FirstOrDefault(t => string.Equals(t.Name, organization, StringComparison.CurrentCultureIgnoreCase));

        return existing is null
            ? await CreateOrganizationAsync(db, organization, displayName, email, password, cancellationToken)
            : await RequestToJoinAsync(existing, displayName, email, password, cancellationToken);
    }

    /// <summary>Organización nueva: su primer usuario queda aprobado como administrador.</summary>
    private async Task<(UserOperationResult, ApplicationUser?, bool)> CreateOrganizationAsync(
        BackupDbContext db, string organization, string displayName, string email, string password, CancellationToken cancellationToken)
    {
        var tenant = new Tenant { Name = organization, CreatedAt = timeProvider.GetUtcNow() };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);

        var (user, errors) = await CreateUserAsync(tenant.Id, displayName, email, password, AppRoles.Admin, pending: false);
        if (user is null)
        {
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync(cancellationToken);
            return (new UserOperationResult(false, errors), null, false);
        }

        LogOrganizationCreated(organization, email);
        return (UserOperationResult.Ok, user, true);
    }

    /// <summary>Organización existente: lector pendiente de aprobación por sus administradores.</summary>
    private async Task<(UserOperationResult, ApplicationUser?, bool)> RequestToJoinAsync(
        Tenant tenant, string displayName, string email, string password, CancellationToken cancellationToken)
    {
        var (user, errors) = await CreateUserAsync(tenant.Id, displayName, email, password, AppRoles.Reader, pending: true);
        if (user is null)
        {
            return (new UserOperationResult(false, errors), null, false);
        }

        LogJoinRequested(tenant.Name, email);
        await NotifyAdminsAsync(tenant, user, cancellationToken);
        return (UserOperationResult.Ok with
        {
            Notice = $"{tenant.Name} ya existe, así que enviamos una solicitud a sus administradores. Podrás entrar cuando la aprueben.",
        }, user, false);
    }

    private async Task<(ApplicationUser? User, IReadOnlyList<string> Errors)> CreateUserAsync(
        Guid tenantId, string displayName, string email, string password, string role, bool pending)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName.Trim(),
            TenantId = tenantId,
            CreatedAt = timeProvider.GetUtcNow(),
            LockoutEnabled = true,
            PendingApproval = pending,
        };

        var created = await users.CreateAsync(user, password);
        var assigned = created.Succeeded ? await users.AddToRoleAsync(user, role) : created;
        if (assigned.Succeeded)
        {
            return (user, []);
        }

        if (created.Succeeded)
        {
            await users.DeleteAsync(user);
        }

        return (null, [.. assigned.Errors.Select(IdentityMessages.Translate)]);
    }

    /// <summary>Habilita un tenant registrado (solo super administradores).</summary>
    public async Task ApproveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        EnsureSuperAdmin();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Tenants.Where(t => t.Id == tenantId && t.PendingApproval)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.PendingApproval, false).SetProperty(t => t.Enabled, true), cancellationToken);
    }

    /// <summary>Elimina una solicitud pendiente y sus usuarios (no tiene trabajos: nunca pudo entrar).</summary>
    public async Task RejectAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        EnsureSuperAdmin();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId && t.PendingApproval, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        // Roles, claims y tokens se eliminan en cascada.
        await db.Users.Where(u => u.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Avisa por correo a los administradores activos de la organización (si hay SMTP disponible).</summary>
    private async Task NotifyAdminsAsync(Tenant tenant, ApplicationUser user, CancellationToken cancellationToken)
    {
        if (await smtpResolver.ForAccountAsync(tenant.Id, cancellationToken) is not { } smtp)
        {
            return;
        }

        var recipients = (await users.GetUsersInRoleAsync(AppRoles.Admin))
            .Where(u => u.TenantId == tenant.Id && u.Email is not null && !u.PendingApproval && !(u.LockoutEnd > timeProvider.GetUtcNow()))
            .Select(u => u.Email!)
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        try
        {
            var message = EmailTemplates.RegistrationPending(recipients, tenant.Name, user.DisplayName, user.Email!, links.Absolute("admin/users"));
            await sender.SendAsync(smtp, message, cancellationToken);
        }
#pragma warning disable CA1031 // La solicitud ya se guardó; el aviso es informativo
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNotifyFailed(ex, tenant.Name);
        }
    }

    private void EnsureSuperAdmin()
    {
        if (!current.IsAuthenticated || !current.IsSuperAdmin)
        {
            throw new UnauthorizedAccessException("Solo un super administrador puede aprobar registros.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Nueva organización registrada: {Organization} ({Email})")]
    private partial void LogOrganizationCreated(string organization, string email);

    [LoggerMessage(Level = LogLevel.Information, Message = "Solicitud para unirse a {Organization}: {Email}")]
    private partial void LogJoinRequested(string organization, string email);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo avisar a los administradores de {Organization} de una solicitud de registro")]
    private partial void LogNotifyFailed(Exception ex, string organization);
}
