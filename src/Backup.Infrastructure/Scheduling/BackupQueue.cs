using System.Collections.Concurrent;
using System.Threading.Channels;
using Backup.Application;
using Backup.Application.Abstractions;
using Backup.Application.Runs;
using Backup.Domain.Runs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.Infrastructure.Scheduling;

/// <summary>Cola en memoria que garantiza a lo sumo una ejecución por trabajo a la vez.</summary>
public sealed class BackupQueue : IBackupQueue
{
    private readonly Channel<(Guid JobId, RunTrigger Trigger)> _channel = Channel.CreateUnbounded<(Guid, RunTrigger)>();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _busy = new();

    public bool TryEnqueue(Guid jobId, RunTrigger trigger)
    {
        var cts = new CancellationTokenSource();
        if (!_busy.TryAdd(jobId, cts))
        {
            cts.Dispose();
            return false;
        }

        return _channel.Writer.TryWrite((jobId, trigger));
    }

    public bool IsBusy(Guid jobId) => _busy.ContainsKey(jobId);

    public bool Cancel(Guid jobId)
    {
        if (_busy.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    internal ChannelReader<(Guid JobId, RunTrigger Trigger)> Reader => _channel.Reader;

    internal CancellationToken GetToken(Guid jobId) =>
        _busy.TryGetValue(jobId, out var cts) ? cts.Token : CancellationToken.None;

    internal void Complete(Guid jobId)
    {
        if (_busy.TryRemove(jobId, out var cts))
        {
            cts.Dispose();
        }
    }
}

/// <summary>Consume la cola respetando el límite de ejecuciones concurrentes.</summary>
public sealed partial class BackupWorker(
    BackupQueue queue,
    IBackupRunner runner,
    IRunRepository runs,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider,
    ILogger<BackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var abandoned = await runs.AbandonRunningAsync(stoppingToken);
        if (abandoned > 0)
        {
            LogAbandoned(abandoned);
        }

        await PruneHistoryAsync(stoppingToken);

        using var slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentRuns));
        var running = new List<Task>();

        try
        {
            await foreach (var (jobId, trigger) in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await slots.WaitAsync(stoppingToken);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, queue.GetToken(jobId));
                        await runner.RunAsync(jobId, trigger, linked.Token);
                    }
#pragma warning disable CA1031 // Errores ya registrados por el runner; el worker sigue vivo
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogRunCrashed(ex, jobId);
                    }
                    finally
                    {
                        queue.Complete(jobId);
                        slots.Release();
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado ordenado.
        }

        await Task.WhenAll(running);
    }

    private async Task PruneHistoryAsync(CancellationToken cancellationToken)
    {
        if (options.Value.RunHistoryDays <= 0)
        {
            return;
        }

        var removed = await runs.PruneAsync(timeProvider.GetUtcNow().AddDays(-options.Value.RunHistoryDays), cancellationToken);
        if (removed > 0)
        {
            LogPruned(removed);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} ejecuciones quedaron interrumpidas por un reinicio")]
    private partial void LogAbandoned(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Historial depurado: {Count} ejecuciones antiguas eliminadas")]
    private partial void LogPruned(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "La ejecución del trabajo {JobId} falló de forma inesperada")]
    private partial void LogRunCrashed(Exception ex, Guid jobId);
}
