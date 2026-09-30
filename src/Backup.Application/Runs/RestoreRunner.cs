using System.Collections.Concurrent;
using Backup.Application.Abstractions;
using Backup.Application.Providers;
using Backup.Domain.Jobs;
using Backup.Domain.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.Application.Runs;

/// <summary>
/// Ejecuta restauraciones en segundo plano (pueden tardar mucho) y va guardando su bitácora para verla en vivo.
/// La configuración del destino, con sus secretos, solo vive en memoria mientras dura la restauración.
/// </summary>
public sealed partial class RestoreRunner(
    IServiceScopeFactory scopes,
    IRestoreRepository restores,
    IProviderRegistry registry,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider,
    ILogger<RestoreRunner> logger)
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public bool IsRunning(Guid id) => _running.ContainsKey(id);

    public bool Cancel(Guid id)
    {
        if (!_running.TryGetValue(id, out var cancellation))
        {
            return false;
        }

        cancellation.Cancel();
        return true;
    }

    /// <param name="target">Configuración ya resuelta (conexión aplicada, secretos en claro).</param>
    public void Start(RestoreOperation restore, ProviderBinding target)
    {
        var cancellation = new CancellationTokenSource();
        _running[restore.Id] = cancellation;
        _ = Task.Run(() => ExecuteAsync(restore, target, cancellation.Token), CancellationToken.None);
    }

    private async Task ExecuteAsync(RestoreOperation restore, ProviderBinding target, CancellationToken cancellationToken)
    {
        var log = new RunLog(logger, $"Restauración {restore.JobName}", timeProvider);
        var workDir = Path.Combine(options.Value.WorkingDirectory, "restores", restore.Id.ToString("N"));
        using var live = new CancellationTokenSource();
        var publishing = PublishLiveAsync(restore, log, live.Token);
        try
        {
            Directory.CreateDirectory(workDir);
            var provider = registry.FindRestoreTarget(target.ProviderKey)
                ?? throw new InvalidOperationException("Ese tipo de destino no admite restauraciones.");

            log.Info($"Respaldo: {restore.ArtifactName}");
            log.Info("Descargando del destino, verificando el SHA-256 y descifrando...");
            PreparedArtifact artifact;
            await using (var scope = scopes.CreateAsyncScope())
            {
                artifact = await scope.ServiceProvider.GetRequiredService<ArtifactService>()
                    .PrepareAsync(restore.RunId, restore.TenantId, decoded: true, cancellationToken);
            }

            log.Info($"Restaurando {artifact.DownloadName} en {restore.TargetSummary}...");
            var clean = await provider.RestoreAsync(
                new RestoreContext(new ProviderSettings(target.Settings), artifact.FilePath, workDir, log), cancellationToken);

            restore.Status = clean ? RunStatus.Succeeded : RunStatus.Warning;
            if (clean)
            {
                log.Info("Restauración completada correctamente.");
            }
            else
            {
                restore.Error = "Terminó con errores no fatales; revisa la bitácora.";
                log.Warn("Restauración completada con advertencias.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            restore.Status = RunStatus.Cancelled;
            log.Warn("Restauración cancelada. Lo que ya se había restaurado queda como está.");
        }
#pragma warning disable CA1031 // Cualquier fallo se registra en la restauración
        catch (Exception ex)
#pragma warning restore CA1031
        {
            restore.Status = RunStatus.Failed;
            restore.Error = ex.Message;
            log.Error(ex);
        }
        finally
        {
            await live.CancelAsync();
            await publishing;
            restore.FinishedAt = timeProvider.GetUtcNow();
            restore.Log = log.ToString();
            await restores.UpdateAsync(restore, CancellationToken.None);
            _running.TryRemove(restore.Id, out var cancellation);
            cancellation?.Dispose();
            TryDelete(workDir);
        }
    }

    private async Task PublishLiveAsync(RestoreOperation restore, RunLog log, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), timeProvider);
        var published = -1;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (log.Version != published)
                {
                    published = log.Version;
                    restore.Log = log.ToString();
                    await restores.UpdateAsync(restore, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Publicar en vivo es un extra: un fallo al guardar no debe cortar la restauración
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLivePublishFailed(ex, restore.Id);
        }
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(ex, directory);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo publicar en vivo la restauración {Id}")]
    private partial void LogLivePublishFailed(Exception ex, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo limpiar el directorio temporal {Path}")]
    private partial void LogCleanupFailed(Exception ex, string path);
}
