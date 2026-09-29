using Backup.Application.Abstractions;
using Backup.Domain.Notifications;
using Backup.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Backup.Infrastructure.Persistence;

public sealed class TenantRepository(IDbContextFactory<BackupDbContext> contextFactory) : ITenantRepository
{
    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tenants.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<Tenant?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Tenants.Update(tenant);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Guarda la configuración SMTP con la contraseña cifrada en reposo.</summary>
public sealed class NotificationSettingsRepository(
    IDbContextFactory<BackupDbContext> contextFactory,
    ISecretProtector protector) : INotificationSettingsRepository
{
    public async Task<NotificationSettings?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (settings is not null && !string.IsNullOrEmpty(settings.Password))
        {
            settings.Password = protector.Unprotect(settings.Password);
        }

        return settings;
    }

    public async Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stored = new NotificationSettings
        {
            TenantId = settings.TenantId,
            Enabled = settings.Enabled,
            UsePlatformSmtp = settings.UsePlatformSmtp,
            SmtpHost = settings.SmtpHost?.Trim(),
            SmtpPort = settings.SmtpPort,
            Security = settings.Security,
            Username = settings.Username?.Trim(),
            Password = string.IsNullOrEmpty(settings.Password) ? null : protector.Protect(settings.Password),
            FromAddress = settings.FromAddress?.Trim(),
            FromName = settings.FromName.Trim(),
            Recipients = settings.Recipients?.Trim(),
            NotifyOnFailure = settings.NotifyOnFailure,
            NotifyOnSuccess = settings.NotifyOnSuccess,
        };

        var exists = await db.NotificationSettings.AnyAsync(s => s.TenantId == settings.TenantId, cancellationToken);
        if (exists)
        {
            db.NotificationSettings.Update(stored);
        }
        else
        {
            db.NotificationSettings.Add(stored);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
