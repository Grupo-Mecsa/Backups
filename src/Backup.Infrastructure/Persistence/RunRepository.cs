using Backup.Application.Abstractions;
using Backup.Domain.Runs;
using Microsoft.EntityFrameworkCore;

namespace Backup.Infrastructure.Persistence;

public sealed class RunRepository(IDbContextFactory<BackupDbContext> contextFactory, TimeProvider timeProvider) : IRunRepository
{
    public async Task AddAsync(BackupRun run, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(BackupRun run, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Runs.Update(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BackupRun?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupRun>> ListAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var runs = db.Runs.AsNoTracking();

        if (query.TenantId is { } tenantId)
        {
            runs = runs.Where(r => r.TenantId == tenantId);
        }

        if (query.JobId is { } jobId)
        {
            runs = runs.Where(r => r.JobId == jobId);
        }

        if (query.Since is { } since)
        {
            runs = runs.Where(r => r.StartedAt >= since);
        }

        // El log puede ser grande: el listado no lo necesita.
        return await runs
            .OrderByDescending(r => r.StartedAt)
            .Take(query.Take)
            .Select(r => new BackupRun
            {
                Id = r.Id,
                TenantId = r.TenantId,
                JobId = r.JobId,
                JobName = r.JobName,
                Trigger = r.Trigger,
                Status = r.Status,
                StartedAt = r.StartedAt,
                FinishedAt = r.FinishedAt,
                ArtifactName = r.ArtifactName,
                SizeBytes = r.SizeBytes,
                Sha256 = r.Sha256,
                DeletedByRetention = r.DeletedByRetention,
                Error = r.Error,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<int> AbandonRunningAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        return await db.Runs
            .Where(r => r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, RunStatus.Failed)
                .SetProperty(r => r.FinishedAt, now)
                .SetProperty(r => r.Error, "Interrumpido por reinicio del servicio."), cancellationToken);
    }

    public async Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Runs.Where(r => r.StartedAt < before && r.Status != RunStatus.Running).ExecuteDeleteAsync(cancellationToken);
    }
}
