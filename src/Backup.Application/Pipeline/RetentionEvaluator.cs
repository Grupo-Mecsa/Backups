using Backup.Application.Providers;
using Backup.Domain.Jobs;

namespace Backup.Application.Pipeline;

/// <summary>Decide qué respaldos eliminar según la política de retención.</summary>
public static class RetentionEvaluator
{
    /// <summary>
    /// Se conservan los <see cref="RetentionPolicy.KeepLast"/> más recientes y cualquiera
    /// más nuevo que <see cref="RetentionPolicy.KeepDays"/>. El resto se elimina.
    /// Los archivos que no siguen la convención de nombres nunca se tocan.
    /// </summary>
    public static IReadOnlyList<StoredBackup> SelectForDeletion(
        IEnumerable<StoredBackup> backups, RetentionPolicy policy, DateTimeOffset now)
    {
        if (policy.IsUnlimited)
        {
            return [];
        }

        var ordered = backups
            .Select(b => (Backup: b, Timestamp: ArtifactNaming.TryParseTimestamp(b.ObjectName)))
            .Where(x => x.Timestamp is not null)
            .OrderByDescending(x => x.Timestamp)
            .ToList();

        var cutoff = policy.KeepDays > 0 ? now.AddDays(-policy.KeepDays) : (DateTimeOffset?)null;

        return [.. ordered
            .Where((x, index) =>
            {
                var keptByCount = policy.KeepLast > 0 && index < policy.KeepLast;
                var keptByAge = cutoff is not null && x.Timestamp >= cutoff;
                return !keptByCount && !keptByAge;
            })
            .Select(x => x.Backup)];
    }
}
