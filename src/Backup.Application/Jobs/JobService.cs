using Backup.Application.Abstractions;
using Backup.Application.Providers;
using Backup.Application.Security;
using Backup.Domain.Jobs;
using Backup.Domain.Runs;

namespace Backup.Application.Jobs;

/// <summary>Trabajo listo para editar en la UI: sin secretos, pero indicando cuáles existen.</summary>
/// <param name="StoredSecrets">Claves de secretos guardados (ver <see cref="JobSecrets.Key"/>).</param>
public sealed record JobDraft(BackupJob Job, bool IsNew, IReadOnlySet<string> StoredSecrets)
{
    public bool HasStoredSecret(string key) => StoredSecrets.Contains(key);
}

public sealed record SaveJobResult(bool Success, IReadOnlyList<ValidationError> Errors, Guid JobId);

public sealed record ConnectionTestResult(bool Success, string Message);

public sealed record BrowseResult(FolderListing? Listing, string? Error)
{
    public static BrowseResult Ok(FolderListing listing) => new(listing, null);
    public static BrowseResult Fail(string error) => new(null, error);
}

/// <summary>
/// Casos de uso de administración de trabajos. Todas las operaciones se limitan al tenant
/// del usuario actual, y las que modifican algo exigen rol de administrador.
/// </summary>
public sealed class JobService(
    IJobRepository repository,
    IProviderRegistry registry,
    IJobValidator validator,
    JobSecrets secrets,
    IScheduleSignal scheduleSignal,
    IBackupQueue queue,
    ICurrentUser user,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<BackupJob>> ListAsync(CancellationToken cancellationToken = default)
    {
        user.EnsureAuthenticated();
        var jobs = await repository.ListAsync(user.TenantId, cancellationToken);
        return [.. jobs.Select(j => secrets.Strip(j).Job).OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public JobDraft NewDraft() =>
        new(new BackupJob { Schedule = "0 2 * * *", TenantId = user.TenantId }, IsNew: true, new HashSet<string>());

    public async Task<JobDraft?> GetForEditAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await GetOwnedAsync(id, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var (stripped, stored) = secrets.Strip(job);
        return new JobDraft(stripped, IsNew: false, stored);
    }

    /// <param name="keepEncryption">Si el usuario dejó vacía la contraseña de cifrado, conservar la almacenada.</param>
    public async Task<SaveJobResult> SaveAsync(BackupJob edited, bool keepEncryption = true, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();

        var job = JobSecrets.Clone(edited);
        job.TenantId = user.TenantId;
        job.Name = job.Name.Trim();
        job.Schedule = string.IsNullOrWhiteSpace(job.Schedule) ? null : job.Schedule.Trim();
        RemoveUnknownSettings(ProviderRole.Source, job.Source);
        RemoveUnknownSettings(ProviderRole.Destination, job.Destination);

        var existing = await repository.GetAsync(job.Id, cancellationToken);
        if (existing is not null && existing.TenantId != user.TenantId)
        {
            throw new UnauthorizedAccessException("El trabajo pertenece a otro tenant.");
        }

        if (existing is not null)
        {
            secrets.MergeFrom(job, existing, keepEncryption);
            job.CreatedAt = existing.CreatedAt;
        }

        var errors = validator.Validate(job, await repository.ListAsync(user.TenantId, cancellationToken));
        if (errors.Count > 0)
        {
            return new SaveJobResult(false, errors, job.Id);
        }

        job.UpdatedAt = timeProvider.GetUtcNow();
        if (existing is null)
        {
            job.CreatedAt = job.UpdatedAt;
            await repository.AddAsync(job, cancellationToken);
        }
        else
        {
            await repository.UpdateAsync(job, cancellationToken);
        }

        scheduleSignal.Changed();
        return new SaveJobResult(true, [], job.Id);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var job = await GetOwnedAsync(id, cancellationToken) ?? throw new KeyNotFoundException();
        job.Enabled = enabled;
        job.UpdatedAt = timeProvider.GetUtcNow();
        await repository.UpdateAsync(job, cancellationToken);
        scheduleSignal.Changed();
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (await GetOwnedAsync(id, cancellationToken) is null)
        {
            return;
        }

        await repository.DeleteAsync(id, cancellationToken);
        scheduleSignal.Changed();
    }

    /// <returns>False si el trabajo ya estaba en cola o en ejecución.</returns>
    public async Task<bool> RunNowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        return await GetOwnedAsync(id, cancellationToken) is not null && queue.TryEnqueue(id, RunTrigger.Manual);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        return await GetOwnedAsync(id, cancellationToken) is not null && queue.Cancel(id);
    }

    public bool IsBusy(Guid id) => queue.IsBusy(id);

    /// <summary>Prueba la conexión de un origen o destino, reutilizando secretos guardados si se dejaron vacíos.</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(
        ProviderRole role, ProviderBinding binding, Guid? jobId, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var provider = registry.Find(role, binding.ProviderKey);
        if (provider is not IConnectionTester tester)
        {
            return new(false, "Este proveedor no permite probar la conexión.");
        }

        var effective = await WithStoredSecretsAsync(role, binding, jobId, cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var message = await tester.TestConnectionAsync(new ProviderSettings(effective.Settings), timeout.Token);
            return new(true, message);
        }
#pragma warning disable CA1031 // El mensaje de error se muestra al usuario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new(false, ex is OperationCanceledException ? "Tiempo de espera agotado." : ex.Message);
        }
    }

    /// <summary>Explora las carpetas de un origen o destino con la configuración que el usuario está editando.</summary>
    public async Task<BrowseResult> BrowseAsync(
        ProviderRole role, ProviderBinding binding, Guid? jobId, string? path, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (registry.Find(role, binding.ProviderKey) is not IFolderBrowser browser)
        {
            return BrowseResult.Fail("Este proveedor no permite explorar carpetas.");
        }

        var effective = await WithStoredSecretsAsync(role, binding, jobId, cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return BrowseResult.Ok(await browser.BrowseAsync(new ProviderSettings(effective.Settings), path, timeout.Token));
        }
#pragma warning disable CA1031 // El mensaje de error se muestra al usuario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return BrowseResult.Fail(ex is OperationCanceledException ? "Tiempo de espera agotado." : ex.Message);
        }
    }

    /// <summary>Trabajo por id solo si pertenece al tenant del usuario.</summary>
    private async Task<BackupJob?> GetOwnedAsync(Guid id, CancellationToken cancellationToken)
    {
        user.EnsureAuthenticated();
        var job = await repository.GetAsync(id, cancellationToken);
        return job is not null && user.Owns(job.TenantId) ? job : null;
    }

    /// <summary>Copia del binding con los secretos guardados cuando el usuario los dejó vacíos.</summary>
    private async Task<ProviderBinding> WithStoredSecretsAsync(
        ProviderRole role, ProviderBinding binding, Guid? jobId, CancellationToken cancellationToken)
    {
        var effective = binding.Clone();
        if (jobId is { } id && await GetOwnedAsync(id, cancellationToken) is { } stored)
        {
            var probe = new BackupJob
            {
                Source = role == ProviderRole.Source ? effective : new ProviderBinding(),
                Destination = role == ProviderRole.Destination ? effective : new ProviderBinding(),
            };
            secrets.MergeFrom(probe, stored, keepEncryption: false);
        }

        return effective;
    }

    private void RemoveUnknownSettings(ProviderRole role, ProviderBinding binding)
    {
        var provider = registry.Find(role, binding.ProviderKey);
        if (provider is null)
        {
            return;
        }

        var known = provider.Descriptor.Fields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in binding.Settings.Keys.Where(k => !known.Contains(k)).ToList())
        {
            binding.Settings.Remove(key);
        }
    }
}
