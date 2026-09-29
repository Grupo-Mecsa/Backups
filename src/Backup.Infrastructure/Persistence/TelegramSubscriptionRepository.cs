using Backup.Application.Telegram;
using Backup.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Backup.Infrastructure.Persistence;

public sealed class TelegramSubscriptionRepository(IDbContextFactory<BackupDbContext> contextFactory) : ITelegramSubscriptionRepository
{
    public async Task<IReadOnlyList<TelegramSubscription>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.TelegramSubscriptions.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.ChatTitle).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TelegramSubscription>> ListByChatAsync(long chatId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.TelegramSubscriptions.AsNoTracking().Where(s => s.ChatId == chatId).ToListAsync(cancellationToken);
    }

    public async Task<TelegramSubscription?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.TelegramSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<TelegramSubscription> UpsertAsync(TelegramSubscription subscription, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.TelegramSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == subscription.TenantId && s.ChatId == subscription.ChatId, cancellationToken);
        if (existing is null)
        {
            db.TelegramSubscriptions.Add(subscription);
            existing = subscription;
        }
        else
        {
            // Volver a vincular un chat conserva sus preferencias; actualiza el nombre y quién lo vinculó.
            existing.ChatTitle = subscription.ChatTitle;
            existing.IsGroup = subscription.IsGroup;
            existing.UserId = subscription.UserId;
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task UpdateAsync(TelegramSubscription subscription, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.TelegramSubscriptions.Update(subscription);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.TelegramSubscriptions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> DeleteByChatAsync(long chatId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.TelegramSubscriptions.Where(s => s.ChatId == chatId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> DeleteByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.TelegramSubscriptions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }
}
