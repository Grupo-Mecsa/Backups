using Backup.Application.Abstractions;
using Backup.Application.Security;
using Backup.Domain.Tenants;

namespace Backup.Application.Tenants;

/// <summary>Administración de tenants (solo SuperAdmin).</summary>
public sealed class TenantService(ITenantRepository repository, ICurrentUser user, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken = default)
    {
        EnsureSuperAdmin();
        return [.. (await repository.ListAsync(cancellationToken)).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Nombre del tenant actual (visible para cualquier usuario autenticado).</summary>
    public async Task<string?> GetCurrentNameAsync(CancellationToken cancellationToken = default) =>
        user.IsAuthenticated ? (await repository.GetAsync(user.TenantId, cancellationToken))?.Name : null;

    public async Task<(bool Success, string? Error, Guid Id)> SaveAsync(Guid? id, string name, bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureSuperAdmin();
        name = name.Trim();
        if (name.Length is 0 or > 100)
        {
            return (false, "El nombre es obligatorio (máx. 100 caracteres).", Guid.Empty);
        }

        var all = await repository.ListAsync(cancellationToken);
        if (all.Any(t => t.Id != id && string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return (false, "Ya existe un tenant con ese nombre.", Guid.Empty);
        }

        if (id is { } existingId)
        {
            var tenant = all.FirstOrDefault(t => t.Id == existingId) ?? throw new KeyNotFoundException();
            if (!enabled && tenant.Id == user.TenantId)
            {
                return (false, "No puedes deshabilitar el tenant en el que estás trabajando.", Guid.Empty);
            }

            tenant.Name = name;
            tenant.Enabled = enabled;
            tenant.PendingApproval &= !enabled; // habilitarlo equivale a aprobarlo
            await repository.UpdateAsync(tenant, cancellationToken);
            return (true, null, tenant.Id);
        }

        var created = new Tenant { Name = name, Enabled = enabled, CreatedAt = timeProvider.GetUtcNow() };
        await repository.AddAsync(created, cancellationToken);
        return (true, null, created.Id);
    }

    private void EnsureSuperAdmin()
    {
        if (!user.IsAuthenticated || !user.IsSuperAdmin)
        {
            throw new UnauthorizedAccessException("Solo un super administrador puede gestionar tenants.");
        }
    }
}
