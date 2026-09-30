using Backup.Domain.Connections;
using Backup.Domain.Jobs;
using Backup.Domain.Notifications;
using Backup.Domain.Runs;
using Backup.Domain.Tenants;

namespace Backup.Application.Abstractions;

public interface IJobRepository
{
    /// <param name="tenantId">Filtra por tenant. Null = todos (solo para procesos de sistema como el planificador).</param>
    Task<IReadOnlyList<BackupJob>> ListAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);
    Task<BackupJob?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(BackupJob job, CancellationToken cancellationToken = default);
    Task UpdateAsync(BackupJob job, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Conexiones reutilizables. Los secretos se guardan cifrados y se devuelven en claro.</summary>
public interface IConnectionRepository
{
    Task<IReadOnlyList<Connection>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<Connection?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(Connection connection, CancellationToken cancellationToken = default);
    Task UpdateAsync(Connection connection, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Historial de restauraciones.</summary>
public interface IRestoreRepository
{
    Task AddAsync(RestoreOperation restore, CancellationToken cancellationToken = default);
    Task UpdateAsync(RestoreOperation restore, CancellationToken cancellationToken = default);
    Task<RestoreOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RestoreOperation>> ListAsync(Guid tenantId, int take = 100, CancellationToken cancellationToken = default);

    /// <summary>Marca como fallidas las restauraciones que quedaron "en curso" tras un reinicio.</summary>
    Task<int> AbandonRunningAsync(CancellationToken cancellationToken = default);
}

public interface IRunRepository
{
    Task AddAsync(BackupRun run, CancellationToken cancellationToken = default);
    Task UpdateAsync(BackupRun run, CancellationToken cancellationToken = default);
    Task<BackupRun?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupRun>> ListAsync(RunQuery query, CancellationToken cancellationToken = default);

    /// <summary>Marca como fallidas las ejecuciones que quedaron "en curso" tras un reinicio.</summary>
    Task<int> AbandonRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>Elimina el historial anterior a la fecha indicada.</summary>
    Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default);

    /// <summary>Borra la ejecución y su bitácora de la base (borrado físico). No borra ejecuciones en curso.</summary>
    /// <returns>False si no existía o seguía en curso.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <param name="TenantId">Null = todos los tenants (procesos de sistema).</param>
public sealed record RunQuery(Guid? JobId = null, DateTimeOffset? Since = null, int Take = 50, Guid? TenantId = null);

public interface ITenantRepository
{
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken = default);
    Task<Tenant?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default);
    Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default);
}

public interface INotificationSettingsRepository
{
    /// <summary>Devuelve la configuración con la contraseña SMTP descifrada, o null si no existe.</summary>
    Task<NotificationSettings?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken = default);
}
