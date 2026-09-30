using Backup.Application.Abstractions;
using Backup.Application.Security;
using Backup.Domain.Jobs;
using Backup.Domain.Runs;

namespace Backup.Application.Runs;

public sealed record UpcomingRun(Guid JobId, string JobName, DateTimeOffset At);

public sealed record DashboardSummary(
    int TotalJobs,
    int ScheduledJobs,
    int RunsLast7Days,
    int FailedLast7Days,
    long BytesLast7Days,
    double? SuccessRate,
    IReadOnlyList<UpcomingRun> Upcoming,
    IReadOnlyList<BackupRun> RecentRuns,
    IReadOnlyList<DailyRunStat> Daily);

public sealed record DailyRunStat(DateOnly Day, int Succeeded, int Failed);

/// <summary>Consultas de solo lectura para el panel principal y el historial.</summary>
public sealed class DashboardService(
    IJobRepository jobs,
    IRunRepository runs,
    IScheduleCalculator schedules,
    ICurrentUser user,
    TimeProvider timeProvider)
{
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        user.EnsureAuthenticated();
        var allJobs = await jobs.ListAsync(user.TenantId, cancellationToken);
        var weekRuns = await runs.ListAsync(new RunQuery(Since: now.AddDays(-7), Take: 5000, TenantId: user.TenantId), cancellationToken);

        var finished = weekRuns.Where(r => r.Status is RunStatus.Succeeded or RunStatus.Warning or RunStatus.Failed).ToList();
        var succeeded = finished.Count(r => r.Status != RunStatus.Failed);

        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().Date);
        var daily = Enumerable.Range(0, 7)
            .Select(offset => today.AddDays(offset - 6))
            .Select(day =>
            {
                var ofDay = finished.Where(r => DateOnly.FromDateTime(r.StartedAt.ToLocalTime().Date) == day).ToList();
                return new DailyRunStat(day, ofDay.Count(r => r.Status is RunStatus.Succeeded or RunStatus.Warning), ofDay.Count(r => r.Status == RunStatus.Failed));
            })
            .ToList();

        return new DashboardSummary(
            TotalJobs: allJobs.Count,
            ScheduledJobs: allJobs.Count(j => j.IsScheduled),
            RunsLast7Days: weekRuns.Count,
            FailedLast7Days: finished.Count - succeeded,
            BytesLast7Days: weekRuns.Sum(r => r.SizeBytes ?? 0),
            SuccessRate: finished.Count == 0 ? null : (double)succeeded / finished.Count,
            Upcoming: GetUpcoming(allJobs, now, 6),
            RecentRuns: [.. weekRuns.OrderByDescending(r => r.StartedAt).Take(8)],
            Daily: daily);
    }

    public IReadOnlyList<UpcomingRun> GetUpcoming(IEnumerable<BackupJob> allJobs, DateTimeOffset now, int take) =>
        [.. allJobs
            .Where(j => j.IsScheduled)
            .Select(j => (Job: j, Next: GetNextRun(j, now)))
            .Where(x => x.Next is not null)
            .OrderBy(x => x.Next)
            .Take(take)
            .Select(x => new UpcomingRun(x.Job.Id, x.Job.Name, x.Next!.Value))];

    public DateTimeOffset? GetNextRun(BackupJob job, DateTimeOffset? from = null) =>
        job.IsScheduled ? schedules.GetNextOccurrence(job.Schedule!, job.TimeZone, from ?? timeProvider.GetUtcNow()) : null;

    public Task<IReadOnlyList<BackupRun>> ListRunsAsync(Guid? jobId, int take = 100, CancellationToken cancellationToken = default) =>
        runs.ListAsync(new RunQuery(jobId, Take: take, TenantId: RequireTenant()), cancellationToken);

    /// <summary>Ejecución por id, solo si pertenece al tenant del usuario.</summary>
    public async Task<BackupRun?> GetRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var run = await runs.GetAsync(id, cancellationToken);
        return run is not null && run.TenantId == RequireTenant() ? run : null;
    }

    /// <summary>¿Debe el usuario actual ver este evento de ejecución?</summary>
    public bool IsVisible(BackupRun run) => user.Owns(run.TenantId);

    private Guid RequireTenant()
    {
        user.EnsureAuthenticated();
        return user.TenantId;
    }

    /// <summary>Última ejecución de cada trabajo.</summary>
    public async Task<IReadOnlyDictionary<Guid, BackupRun>> GetLastRunsAsync(CancellationToken cancellationToken = default)
    {
        var recent = await runs.ListAsync(new RunQuery(Take: 1000, TenantId: RequireTenant()), cancellationToken);
        return recent
            .GroupBy(r => r.JobId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First());
    }
}
