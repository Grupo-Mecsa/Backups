using Backup.Application.Abstractions;
using Backup.Domain.Runs;
using Microsoft.EntityFrameworkCore;

namespace Backup.Infrastructure.Persistence;

public sealed class RestoreRepository(IDbContextFactory<BackupDbContext> contextFactory, TimeProvider timeProvider) : IRestoreRepository
{
    public async Task AddAsync(RestoreOperation restore, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Restores.Add(restore);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(RestoreOperation restore, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Restores.Update(restore);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RestoreOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Restores.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<RestoreOperation>> ListAsync(Guid tenantId, int take = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Sin la bitácora: la lista no la necesita y puede ser larga.
        return await db.Restores.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .Select(r => new RestoreOperation
            {
                Id = r.Id,
                TenantId = r.TenantId,
                RunId = r.RunId,
                JobName = r.JobName,
                ArtifactName = r.ArtifactName,
                TargetProvider = r.TargetProvider,
                TargetSummary = r.TargetSummary,
                RequestedBy = r.RequestedBy,
                Status = r.Status,
                StartedAt = r.StartedAt,
                FinishedAt = r.FinishedAt,
                Error = r.Error,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<int> AbandonRunningAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        return await db.Restores
            .Where(r => r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, RunStatus.Failed)
                .SetProperty(r => r.FinishedAt, now)
                .SetProperty(r => r.Error, "Interrumpida por reinicio del servicio: revisa el destino, puede haber quedado a medias."), cancellationToken);
    }
}
