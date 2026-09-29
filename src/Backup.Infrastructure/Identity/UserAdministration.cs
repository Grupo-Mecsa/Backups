using Backup.Application.Security;
using Backup.Application.Telegram;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Infrastructure.Identity;

/// <summary>
/// Gestión de usuarios del tenant actual con ASP.NET Core Identity. Reglas:
/// solo un SuperAdmin asigna o edita SuperAdmins; nadie se bloquea, elimina ni degrada a sí mismo;
/// cada tenant conserva al menos un administrador.
/// </summary>
internal sealed class UserOperations(
    UserManager<ApplicationUser> users,
    PasswordRecovery recovery,
    ITelegramSubscriptionRepository telegram,
    ICurrentUser current,
    TimeProvider timeProvider) : IUserAdministration
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        current.EnsureCanManage();
        var tenantUsers = await users.Users.AsNoTracking()
            .Where(u => u.TenantId == current.TenantId)
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);

        var result = new List<UserSummary>(tenantUsers.Count);
        foreach (var user in tenantUsers)
        {
            result.Add(await ToSummaryAsync(user));
        }

        return result;
    }

    public async Task<UserOperationResult> SaveAsync(UserEdit edit, CancellationToken cancellationToken = default)
    {
        current.EnsureCanManage();
        if (Validate(edit) is { } invalid)
        {
            return invalid;
        }

        if (edit.Role == AppRoles.SuperAdmin && !current.IsSuperAdmin)
        {
            return UserOperationResult.Fail("Solo un super administrador puede asignar ese rol.");
        }

        return edit.Id is null
            ? await CreateAsync(edit, current.TenantId, cancellationToken)
            : await UpdateAsync(edit, edit.Email.Trim());
    }

    public async Task<UserOperationResult> CreateInTenantAsync(Guid tenantId, UserEdit edit, CancellationToken cancellationToken = default)
    {
        if (!current.IsAuthenticated || !current.IsSuperAdmin)
        {
            throw new UnauthorizedAccessException("Solo un super administrador puede crear usuarios en otro tenant.");
        }

        edit = edit with { Id = null };
        return Validate(edit) ?? await CreateAsync(edit, tenantId, cancellationToken);
    }

    public async Task<UserOperationResult> SendAccessLinkAsync(string userId, CancellationToken cancellationToken = default)
    {
        current.EnsureCanManage();
        var (user, error) = await FindEditableAsync(userId);
        if (user is null)
        {
            return error!;
        }

        try
        {
            // Quien nunca entró recibe la invitación; el resto, un enlace de restablecimiento.
            if (user.LastLoginAt is null)
            {
                await recovery.SendInvitationAsync(user, current.DisplayName, cancellationToken);
            }
            else
            {
                await recovery.SendResetAsync(user, cancellationToken);
            }

            return UserOperationResult.Ok with { Notice = $"Enlace de acceso enviado a {user.Email}." };
        }
#pragma warning disable CA1031 // El error de SMTP se muestra al administrador
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return UserOperationResult.Fail(ex.Message);
        }
    }

    public Task<bool> CanSendEmailAsync(CancellationToken cancellationToken = default) =>
        recovery.CanSendAsync(current.TenantId, cancellationToken);

    private static UserOperationResult? Validate(UserEdit edit)
    {
        var email = edit.Email.Trim();
        if (string.IsNullOrWhiteSpace(email) || !System.Net.Mail.MailAddress.TryCreate(email, out _))
        {
            return UserOperationResult.Fail("Indica un correo válido.");
        }

        if (!AppRoles.All.Contains(edit.Role))
        {
            return UserOperationResult.Fail("Rol inválido.");
        }

        return null;
    }

    public async Task<UserOperationResult> SetLockedAsync(string userId, bool locked, CancellationToken cancellationToken = default)
    {
        current.EnsureCanManage();
        var (user, error) = await FindEditableAsync(userId);
        if (user is null)
        {
            return error!;
        }

        if (user.Id == current.UserId)
        {
            return UserOperationResult.Fail("No puedes bloquear tu propia cuenta.");
        }

        if (locked && await IsLastAdminAsync(user))
        {
            return UserOperationResult.Fail("El tenant debe conservar al menos un administrador activo.");
        }

        await users.SetLockoutEnabledAsync(user, true);
        var result = await users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null);
        await users.UpdateSecurityStampAsync(user); // cierra las sesiones abiertas
        return From(result);
    }

    public async Task<UserOperationResult> DeleteAsync(string userId, CancellationToken cancellationToken = default)
    {
        current.EnsureCanManage();
        var (user, error) = await FindEditableAsync(userId);
        if (user is null)
        {
            return error!;
        }

        if (user.Id == current.UserId)
        {
            return UserOperationResult.Fail("No puedes eliminar tu propia cuenta.");
        }

        if (await IsLastAdminAsync(user))
        {
            return UserOperationResult.Fail("El tenant debe conservar al menos un administrador.");
        }

        var deleted = await users.DeleteAsync(user);
        if (deleted.Succeeded)
        {
            await telegram.DeleteByUserAsync(user.Id, cancellationToken);
        }

        return From(deleted);
    }

    public async Task<UserOperationResult> ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        current.EnsureAuthenticated();
        var user = await users.FindByIdAsync(current.UserId!);
        return user is null
            ? UserOperationResult.Fail("Usuario no encontrado.")
            : From(await users.ChangePasswordAsync(user, currentPassword, newPassword));
    }

    private async Task<UserOperationResult> CreateAsync(UserEdit edit, Guid tenantId, CancellationToken cancellationToken)
    {
        var email = edit.Email.Trim();
        if (edit.SendInvitation)
        {
            if (!await recovery.CanSendAsync(tenantId, cancellationToken))
            {
                return UserOperationResult.Fail("No hay un servidor SMTP para enviar la invitación. Indica una contraseña inicial o configura el correo.");
            }
        }
        else if (string.IsNullOrEmpty(edit.Password))
        {
            return UserOperationResult.Fail("Indica una contraseña inicial.");
        }

        if (await users.FindByEmailAsync(email) is not null)
        {
            return UserOperationResult.Fail("Ya existe un usuario con ese correo.");
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = edit.DisplayName.Trim(),
            TenantId = tenantId,
            CreatedAt = timeProvider.GetUtcNow(),
            LockoutEnabled = true,
        };

        // Con invitación y sin contraseña se asigna una aleatoria que nadie conoce; el usuario define la suya con el enlace.
        var password = string.IsNullOrEmpty(edit.Password) ? IdentitySeeder.GeneratePassword() + IdentitySeeder.GeneratePassword() : edit.Password;
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return From(created);
        }

        var role = await users.AddToRoleAsync(user, edit.Role);
        if (!role.Succeeded || !edit.SendInvitation)
        {
            return From(role);
        }

        try
        {
            await recovery.SendInvitationAsync(user, current.DisplayName, cancellationToken);
            return UserOperationResult.Ok with { Notice = $"Invitación enviada a {email}." };
        }
#pragma warning disable CA1031 // El usuario ya existe; el administrador puede reenviar la invitación
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return UserOperationResult.Ok with { Notice = $"Usuario creado, pero no se pudo enviar la invitación: {ex.Message}" };
        }
    }

    private async Task<UserOperationResult> UpdateAsync(UserEdit edit, string email)
    {
        var (user, error) = await FindEditableAsync(edit.Id!);
        if (user is null)
        {
            return error!;
        }

        var currentRole = (await users.GetRolesAsync(user)).FirstOrDefault() ?? AppRoles.Reader;
        if (currentRole != edit.Role)
        {
            if (user.Id == current.UserId)
            {
                return UserOperationResult.Fail("No puedes cambiar tu propio rol.");
            }

            if (edit.Role == AppRoles.Reader && await IsLastAdminAsync(user))
            {
                return UserOperationResult.Fail("El tenant debe conservar al menos un administrador.");
            }
        }

        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)
            && await users.FindByEmailAsync(email) is { } other && other.Id != user.Id)
        {
            return UserOperationResult.Fail("Ya existe un usuario con ese correo.");
        }

        user.Email = email;
        user.UserName = email;
        user.DisplayName = edit.DisplayName.Trim();
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return From(updated);
        }

        if (currentRole != edit.Role)
        {
            await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user));
            await users.AddToRoleAsync(user, edit.Role);
            await users.UpdateSecurityStampAsync(user);
        }

        if (!string.IsNullOrEmpty(edit.Password))
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            return From(await users.ResetPasswordAsync(user, token, edit.Password));
        }

        return UserOperationResult.Ok;
    }

    /// <summary>Usuario del tenant actual que el usuario en sesión puede modificar.</summary>
    private async Task<(ApplicationUser? User, UserOperationResult? Error)> FindEditableAsync(string userId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null || user.TenantId != current.TenantId)
        {
            return (null, UserOperationResult.Fail("Usuario no encontrado."));
        }

        if (!current.IsSuperAdmin && await users.IsInRoleAsync(user, AppRoles.SuperAdmin))
        {
            return (null, UserOperationResult.Fail("Solo un super administrador puede modificar esta cuenta."));
        }

        return (user, null);
    }

    private async Task<bool> IsLastAdminAsync(ApplicationUser user)
    {
        var roles = await users.GetRolesAsync(user);
        if (!roles.Any(r => r is AppRoles.Admin or AppRoles.SuperAdmin))
        {
            return false;
        }

        var admins = (await users.GetUsersInRoleAsync(AppRoles.Admin))
            .Concat(await users.GetUsersInRoleAsync(AppRoles.SuperAdmin))
            .Where(u => u.TenantId == user.TenantId && u.Id != user.Id && !(u.LockoutEnd > timeProvider.GetUtcNow()));
        return !admins.Any();
    }

    private async Task<UserSummary> ToSummaryAsync(ApplicationUser user) => new(
        user.Id,
        user.Email ?? user.UserName ?? string.Empty,
        user.DisplayName,
        (await users.GetRolesAsync(user)).FirstOrDefault() ?? AppRoles.Reader,
        user.TenantId,
        user.LockoutEnd > timeProvider.GetUtcNow(),
        user.LastLoginAt);

    private static UserOperationResult From(IdentityResult result) =>
        result.Succeeded ? UserOperationResult.Ok : new UserOperationResult(false, [.. result.Errors.Select(e => IdentityMessages.Translate(e))]);
}

/// <summary>
/// Cada operación usa su propio scope (y DbContext): en Blazor Server el scope del circuito
/// vive mientras el usuario tiene la página abierta y acumularía entidades obsoletas.
/// </summary>
public sealed class UserAdministration(IServiceScopeFactory scopes, ICurrentUser current, IAccountLinks links, TimeProvider timeProvider) : IUserAdministration
{
    public Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.ListAsync(cancellationToken));

    public Task<UserOperationResult> SaveAsync(UserEdit edit, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.SaveAsync(edit, cancellationToken));

    public Task<UserOperationResult> SetLockedAsync(string userId, bool locked, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.SetLockedAsync(userId, locked, cancellationToken));

    public Task<UserOperationResult> DeleteAsync(string userId, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.DeleteAsync(userId, cancellationToken));

    public Task<UserOperationResult> ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.ChangeOwnPasswordAsync(currentPassword, newPassword, cancellationToken));

    public Task<UserOperationResult> CreateInTenantAsync(Guid tenantId, UserEdit edit, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CreateInTenantAsync(tenantId, edit, cancellationToken));

    public Task<UserOperationResult> SendAccessLinkAsync(string userId, CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.SendAccessLinkAsync(userId, cancellationToken));

    public Task<bool> CanSendEmailAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ops => ops.CanSendEmailAsync(cancellationToken));

    private async Task<T> RunAsync<T>(Func<UserOperations, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Los enlaces usan el IAccountLinks del circuito (conoce la URL base); el resto sale del scope nuevo.
        var recovery = ActivatorUtilities.CreateInstance<PasswordRecovery>(scope.ServiceProvider, users, links);
        var telegram = scope.ServiceProvider.GetRequiredService<ITelegramSubscriptionRepository>();
        return await action(new UserOperations(users, recovery, telegram, current, timeProvider));
    }
}
