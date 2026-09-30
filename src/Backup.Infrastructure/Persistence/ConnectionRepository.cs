using System.Text.Json;
using Backup.Application.Abstractions;
using Backup.Application.Providers;
using Backup.Domain.Connections;
using Microsoft.EntityFrameworkCore;

namespace Backup.Infrastructure.Persistence;

/// <summary>
/// Repositorio de conexiones. Como en los trabajos, los campos secretos se cifran al guardar y se descifran al
/// leer, así el resto de la aplicación trabaja con valores en claro.
/// </summary>
public sealed class ConnectionRepository(
    IDbContextFactory<BackupDbContext> contextFactory,
    ISecretProtector protector,
    IProviderRegistry registry) : IConnectionRepository
{
    public async Task<IReadOnlyList<Connection>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.Connections.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync(cancellationToken);
        return [.. records.Select(ToDomain)];
    }

    public async Task<Connection?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return record is null ? null : ToDomain(record);
    }

    public async Task AddAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Connections.Add(ToRecord(connection));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Connections.Update(ToRecord(connection));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Connections.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    private IEnumerable<string> SecretKeys(string providerKey) =>
        registry.FindAny(providerKey)?.Descriptor.ConnectionFields.Where(f => f.IsSecret).Select(f => f.Key) ?? [];

    private ConnectionRecord ToRecord(Connection connection)
    {
        var settings = new Dictionary<string, string?>(connection.Settings, StringComparer.OrdinalIgnoreCase);
        foreach (var key in SecretKeys(connection.ProviderKey))
        {
            if (settings.GetValueOrDefault(key) is { Length: > 0 } value)
            {
                settings[key] = protector.Protect(value);
            }
        }

        return new ConnectionRecord
        {
            Id = connection.Id,
            TenantId = connection.TenantId,
            Name = connection.Name,
            ProviderKey = connection.ProviderKey,
            SettingsJson = JsonSerializer.Serialize(settings),
            CreatedAt = connection.CreatedAt,
            UpdatedAt = connection.UpdatedAt,
        };
    }

    private Connection ToDomain(ConnectionRecord record)
    {
        var settings = new Dictionary<string, string?>(
            JsonSerializer.Deserialize<Dictionary<string, string?>>(record.SettingsJson) ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var key in SecretKeys(record.ProviderKey))
        {
            if (settings.GetValueOrDefault(key) is { Length: > 0 } value)
            {
                settings[key] = protector.Unprotect(value);
            }
        }

        return new Connection
        {
            Id = record.Id,
            TenantId = record.TenantId,
            Name = record.Name,
            ProviderKey = record.ProviderKey,
            Settings = settings,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };
    }
}
