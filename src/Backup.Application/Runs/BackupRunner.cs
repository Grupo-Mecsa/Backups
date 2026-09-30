using Backup.Application.Abstractions;
using Backup.Application.Pipeline;
using Backup.Application.Providers;
using Backup.Domain.Jobs;
using Backup.Domain.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.Application.Runs;

public interface IBackupRunner
{
    Task<BackupRun> RunAsync(Guid jobId, RunTrigger trigger, CancellationToken cancellationToken);
}

/// <summary>
/// Orquesta una ejecución: origen → transformaciones → destino → retención.
/// Solo coordina; cada paso lo resuelve una abstracción distinta (SRP/DIP).
/// </summary>
public sealed partial class BackupRunner(
    IJobRepository jobs,
    IRunRepository runs,
    IProviderRegistry registry,
    ArtifactPackager packager,
    Connections.ConnectionResolver connections,
    IEnumerable<IRunNotifier> notifiers,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider,
    ILogger<BackupRunner> logger) : IBackupRunner
{
    public async Task<BackupRun> RunAsync(Guid jobId, RunTrigger trigger, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken)
            ?? throw new KeyNotFoundException($"No existe el trabajo {jobId}.");

        var run = new BackupRun
        {
            JobId = job.Id,
            TenantId = job.TenantId,
            JobName = job.Name,
            Trigger = trigger,
            StartedAt = timeProvider.GetUtcNow(),
        };
        var log = new RunLog(logger, job.Name, timeProvider);

        await runs.AddAsync(run, cancellationToken);
        await NotifyAsync(run);

        var workDir = Path.Combine(options.Value.WorkingDirectory, run.Id.ToString("N"));
        try
        {
            Directory.CreateDirectory(workDir);
            using (var live = new CancellationTokenSource())
            {
                var publishing = PublishLiveAsync(run, log, live.Token);
                try
                {
                    await ExecuteAsync(job, run, log, workDir, cancellationToken);
                }
                finally
                {
                    await live.CancelAsync();
                    await publishing;
                }
            }

            if (log.Omitted > 0)
            {
                run.Status = RunStatus.Warning;
                run.Error = $"{log.Omitted} elemento(s) omitido(s); revisa la bitácora.";
                log.Warn($"Respaldo completado con advertencias: {run.Error}");
            }
            else
            {
                run.Status = RunStatus.Succeeded;
                log.Info("Respaldo completado correctamente.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = RunStatus.Cancelled;
            log.Warn("Ejecución cancelada.");
        }
#pragma warning disable CA1031 // Cualquier fallo se registra en la ejecución
        catch (Exception ex)
#pragma warning restore CA1031
        {
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
            log.Error(ex);
            LogRunFailed(ex, job.Name);
        }
        finally
        {
            run.FinishedAt = timeProvider.GetUtcNow();
            run.Log = log.ToString();
            await runs.UpdateAsync(run, CancellationToken.None);
            await NotifyAsync(run);
            TryDeleteDirectory(workDir);
        }

        return run;
    }

    private async Task ExecuteAsync(BackupJob job, BackupRun run, RunLog log, string workDir, CancellationToken cancellationToken)
    {
        job = await connections.ResolveAsync(job, cancellationToken);
        var source = registry.GetSource(job.Source.ProviderKey);
        var destination = registry.GetDestination(job.Destination.ProviderKey);
        log.Info($"Origen: {source.Descriptor.DisplayName} → Destino: {destination.Descriptor.DisplayName}");
        await CheckpointAsync(run, log);

        var sourceContext = new SourceContext(
            new ProviderSettings(job.Source.Settings), workDir, ArtifactNaming.BaseName(job.Name, run.StartedAt), log);
        var artifact = await source.CreateArtifactAsync(sourceContext, cancellationToken);
        log.Info($"Artefacto generado: {Path.GetFileName(artifact.FilePath)} ({ByteSize.Format(new FileInfo(artifact.FilePath).Length)})");
        await CheckpointAsync(run, log);

        var packaged = await packager.PackageAsync(job, artifact, log, cancellationToken);
        var objectName = ArtifactNaming.ObjectName(job.Name, run.StartedAt, packaged.Extensions);
        run.ArtifactName = objectName;
        run.SizeBytes = packaged.Size;
        run.Sha256 = packaged.Sha256;
        log.Info($"Subiendo {objectName} ({ByteSize.Format(packaged.Size)})...");
        await CheckpointAsync(run, log);

        var destinationContext = new DestinationContext(new ProviderSettings(job.Destination.Settings), log);
        await destination.UploadAsync(destinationContext, packaged.FilePath, objectName, cancellationToken);
        log.Info($"Subida completada. SHA-256: {packaged.Sha256}");
        await CheckpointAsync(run, log);

        run.DeletedByRetention = await ApplyRetentionAsync(job, destination, destinationContext, objectName, log, cancellationToken);
    }

    private async Task<int> ApplyRetentionAsync(
        BackupJob job, IBackupDestination destination, DestinationContext context,
        string currentObject, RunLog log, CancellationToken cancellationToken)
    {
        if (job.Retention.IsUnlimited)
        {
            return 0;
        }

        try
        {
            var folder = ArtifactNaming.Folder(job.Name);
            var prefix = folder + "_";
            var existing = (await destination.ListAsync(context, folder, cancellationToken))
                .Where(b => Path.GetFileName(b.ObjectName).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var toDelete = RetentionEvaluator.SelectForDeletion(existing, job.Retention, timeProvider.GetUtcNow())
                .Where(b => !string.Equals(b.ObjectName, currentObject, StringComparison.Ordinal))
                .ToList();

            foreach (var backup in toDelete)
            {
                await destination.DeleteAsync(context, backup.ObjectName, cancellationToken);
                log.Info($"Retención: eliminado {backup.ObjectName}");
            }

            log.Info($"Retención aplicada: {existing.Count - toDelete.Count} conservados, {toDelete.Count} eliminados.");
            return toDelete.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Un fallo de retención no invalida el respaldo ya subido
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Warn($"No se pudo aplicar la retención: {ex.Message}");
            return 0;
        }
    }

    private async Task CheckpointAsync(BackupRun run, RunLog log)
    {
        await log.PublishGate.WaitAsync();
        try
        {
            run.Log = log.ToString();
            await runs.UpdateAsync(run, CancellationToken.None);
            await NotifyAsync(run);
        }
        finally
        {
            log.PublishGate.Release();
        }
    }

    /// <summary>
    /// Publica la bitácora cada pocos segundos mientras la ejecución avanza: una fase larga (p. ej. descargar miles
    /// de archivos) no pasa por ningún punto de control y, sin esto, la vista en vivo quedaría quieta.
    /// </summary>
    private async Task PublishLiveAsync(BackupRun run, RunLog log, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3), timeProvider);
        var published = log.Version;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (log.Version != published)
                {
                    published = log.Version;
                    await CheckpointAsync(run, log);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Publicar en vivo es un extra: un fallo al guardar no debe tumbar el respaldo
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLivePublishFailed(ex, run.JobName);
        }
    }

    private async Task NotifyAsync(BackupRun run)
    {
        foreach (var notifier in notifiers)
        {
            try
            {
                await notifier.NotifyAsync(run, CancellationToken.None);
            }
#pragma warning disable CA1031 // Un notificador defectuoso no debe romper el respaldo
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogNotifierFailed(ex, notifier.GetType().Name);
            }
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            LogCleanupFailed(ex, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogCleanupFailed(ex, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "El respaldo '{Job}' falló")]
    private partial void LogRunFailed(Exception ex, string job);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El notificador {Notifier} falló")]
    private partial void LogNotifierFailed(Exception ex, string notifier);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo publicar en vivo la bitácora de '{Job}'")]
    private partial void LogLivePublishFailed(Exception ex, string job);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo limpiar el directorio temporal {Path}")]
    private partial void LogCleanupFailed(Exception ex, string path);
}
