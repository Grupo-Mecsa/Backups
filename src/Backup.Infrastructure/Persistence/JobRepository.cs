using System.Text.Json;
using Backup.Application.Abstractions;
using Backup.Application.Jobs;
using Backup.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Backup.Infrastructure.Persistence;

/// <summary>
/// Repositorio de trabajos. Los campos secretos se cifran al guardar y se descifran al leer,
/// de modo que el resto de la aplicación trabaja siempre con valores en claro.
/// </summary>
public sealed class JobRepository(
    IDbContextFactory<BackupDbContext> contextFactory,
    ISecretProtector protector,
    JobSecrets secrets) : IJobRepository
{
    public async Task<IReadOnlyList<BackupJob>> ListAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Jobs.AsNoTracking();
        if (tenantId is { } tenant)
        {
            query = query.Where(j => j.TenantId == tenant);
        }

        var records = await query.ToListAsync(cancellationToken);
        return [.. records.Select(ToDomain)];
    }

    public async Task<BackupJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        return record is null ? null : ToDomain(record);
    }

    public async Task AddAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Jobs.Add(ToRecord(job));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Jobs.Update(ToRecord(job));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Jobs.Where(j => j.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    private JobRecord ToRecord(BackupJob job)
    {
        var copy = JobSecrets.Clone(job);
        foreach (var (_, binding, field) in secrets.Enumerate(copy))
        {
            if (binding.Settings.TryGetValue(field, out var value) && !string.IsNullOrEmpty(value))
            {
                binding.Settings[field] = protector.Protect(value);
            }
        }

        return new JobRecord
        {
            Id = copy.Id,
            TenantId = copy.TenantId,
            Name = copy.Name,
            Description = copy.Description,
            Enabled = copy.Enabled,
            SourceProvider = copy.Source.ProviderKey,
            SourceSettingsJson = JsonSerializer.Serialize(copy.Source.Settings),
            DestinationProvider = copy.Destination.ProviderKey,
            DestinationSettingsJson = JsonSerializer.Serialize(copy.Destination.Settings),
            Schedule = copy.Schedule,
            TimeZone = copy.TimeZone,
            Compression = copy.Compression,
            EncryptionPassphrase = copy.IsEncrypted ? protector.Protect(copy.EncryptionPassphrase!) : null,
            KeepLast = copy.Retention.KeepLast,
            KeepDays = copy.Retention.KeepDays,
            CreatedAt = copy.CreatedAt,
            UpdatedAt = copy.UpdatedAt,
        };
    }

    private BackupJob ToDomain(JobRecord record)
    {
        var job = new BackupJob
        {
            Id = record.Id,
            TenantId = record.TenantId,
            Name = record.Name,
            Description = record.Description,
            Enabled = record.Enabled,
            Source = new ProviderBinding { ProviderKey = record.SourceProvider, Settings = Deserialize(record.SourceSettingsJson) },
            Destination = new ProviderBinding { ProviderKey = record.DestinationProvider, Settings = Deserialize(record.DestinationSettingsJson) },
            Schedule = record.Schedule,
            TimeZone = record.TimeZone,
            Compression = record.Compression,
            EncryptionPassphrase = string.IsNullOrEmpty(record.EncryptionPassphrase) ? null : protector.Unprotect(record.EncryptionPassphrase),
            Retention = new RetentionPolicy { KeepLast = record.KeepLast, KeepDays = record.KeepDays },
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };

        foreach (var (_, binding, field) in secrets.Enumerate(job))
        {
            if (binding.Settings.TryGetValue(field, out var value) && !string.IsNullOrEmpty(value))
            {
                binding.Settings[field] = protector.Unprotect(value);
            }
        }

        return job;
    }

    private static Dictionary<string, string?> Deserialize(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [], StringComparer.OrdinalIgnoreCase);
}
