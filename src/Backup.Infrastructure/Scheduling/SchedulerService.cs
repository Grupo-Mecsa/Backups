using Backup.Application.Abstractions;
using Backup.Domain.Runs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backup.Infrastructure.Scheduling;

/// <summary>Señal que despierta al planificador cuando cambian los trabajos.</summary>
public sealed class ScheduleSignal : IScheduleSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Changed()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Ya había una señal pendiente.
            }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Calcula la próxima ejecución de cada trabajo y lo encola al llegar la hora.
/// Las ejecuciones perdidas mientras el servicio estaba detenido no se recuperan.
/// </summary>
public sealed partial class SchedulerService(
    IJobRepository jobs,
    IScheduleCalculator calculator,
    IBackupQueue queue,
    ScheduleSignal signal,
    TimeProvider timeProvider,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(1);
    private readonly Dictionary<Guid, Entry> _entries = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
                FireDue();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // El planificador nunca debe detenerse por un error puntual
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSchedulerError(ex);
            }

            var now = timeProvider.GetUtcNow();
            var next = _entries.Values.Select(e => e.Next).DefaultIfEmpty(now + MaxSleep).Min();
            var delay = next - now;
            delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxSleep ? MaxSleep : delay;

            try
            {
                await signal.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var scheduled = (await jobs.ListAsync(null, cancellationToken)).Where(j => j.IsScheduled).ToList();

        foreach (var id in _entries.Keys.Except(scheduled.Select(j => j.Id)).ToList())
        {
            _entries.Remove(id);
        }

        foreach (var job in scheduled)
        {
            // Conservar la próxima hora calculada si el horario no cambió, para no saltarse ejecuciones.
            if (_entries.TryGetValue(job.Id, out var entry) && entry.Schedule == job.Schedule && entry.TimeZone == job.TimeZone)
            {
                continue;
            }

            if (calculator.GetNextOccurrence(job.Schedule!, job.TimeZone, now) is { } next)
            {
                _entries[job.Id] = new Entry(job.Name, job.Schedule!, job.TimeZone, next);
            }
            else
            {
                _entries.Remove(job.Id);
            }
        }
    }

    private void FireDue()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (id, entry) in _entries.Where(e => e.Value.Next <= now).ToList())
        {
            if (queue.TryEnqueue(id, RunTrigger.Scheduled))
            {
                LogEnqueued(entry.Name);
            }
            else
            {
                LogSkipped(entry.Name);
            }

            var next = calculator.GetNextOccurrence(entry.Schedule, entry.TimeZone, now);
            if (next is null)
            {
                _entries.Remove(id);
            }
            else
            {
                _entries[id] = entry with { Next = next.Value };
            }
        }
    }

    private sealed record Entry(string Name, string Schedule, string TimeZone, DateTimeOffset Next);

    [LoggerMessage(Level = LogLevel.Information, Message = "Respaldo programado encolado: {Job}")]
    private partial void LogEnqueued(string job);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se omitió la ejecución programada de {Job}: ya estaba en curso")]
    private partial void LogSkipped(string job);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error en el planificador")]
    private partial void LogSchedulerError(Exception ex);
}
