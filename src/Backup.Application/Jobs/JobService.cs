using Backup.Application.Abstractions;
using Backup.Application.Connections;
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

/// <param name="Path">Ruta de la carpeta creada; null si falló.</param>
public sealed record CreateFolderResult(string? Path, string? Error);

public sealed record OptionsResult(IReadOnlyList<string>? Values, string? Error)
{
    public static OptionsResult Ok(IReadOnlyList<string> values) => new(values, null);
    public static OptionsResult Fail(string error) => new(null, error);
}

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
    IRunRepository runs,
    IProviderRegistry registry,
    IJobValidator validator,
    JobSecrets secrets,
    ConnectionResolver connectionResolver,
    ConnectionService connections,
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
        CleanRestoreTarget(job);

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

        // Con conexión guardada, los datos de acceso viven en ella: el trabajo solo guarda lo propio.
        StripConnectionFields(ProviderRole.Source, job.Source);
        StripConnectionFields(ProviderRole.Destination, job.Destination);
        if (job.RestoreTarget is { ConnectionId: not null } restoreTarget && registry.FindRestoreTarget(restoreTarget.ProviderKey) is { } restoreProvider)
        {
            foreach (var field in restoreProvider.Descriptor.ConnectionFields)
            {
                restoreTarget.Settings.Remove(field.Key);
            }
        }

        var errors = new List<ValidationError>();
        BackupJob resolved;
        try
        {
            resolved = await connectionResolver.ResolveAsync(job, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(new(job.Source.ConnectionId is not null ? "Source" : "Destination", ex.Message));
            resolved = job;
        }

        errors.AddRange(validator.Validate(resolved, await repository.ListAsync(user.TenantId, cancellationToken)));
        errors.AddRange(await ValidateRestoreTargetAsync(job, cancellationToken));
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

    /// <summary>
    /// Copia un trabajo con toda su configuración (secretos y conexiones incluidos) bajo un nombre nuevo. La copia
    /// queda pausada para que no se ejecute según el horario antes de revisarla.
    /// </summary>
    /// <returns>Id de la copia.</returns>
    public async Task<Guid> DuplicateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var job = await GetOwnedAsync(id, cancellationToken) ?? throw new KeyNotFoundException();

        var copy = JobSecrets.Clone(job);
        copy.Id = Guid.NewGuid();
        copy.Enabled = false;
        var names = (await repository.ListAsync(user.TenantId, cancellationToken)).Select(j => j.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        copy.Name = CopyName(job.Name, names);
        copy.CreatedAt = copy.UpdatedAt = timeProvider.GetUtcNow();

        await repository.AddAsync(copy, cancellationToken);
        scheduleSignal.Changed();
        return copy.Id;
    }

    /// <summary>"Nombre (copia)", "Nombre (copia 2)"... sin pasar del largo máximo del nombre.</summary>
    internal static string CopyName(string name, IReadOnlySet<string> taken)
    {
        const int MaxLength = 100;
        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? " (copia)" : $" (copia {i})";
            var candidate = name[..Math.Min(name.Length, MaxLength - suffix.Length)].TrimEnd() + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Borra definitivamente una ejecución terminada del historial (el artefacto en el destino no se toca).</summary>
    /// <returns>False si no existe, es de otro tenant o sigue en curso.</returns>
    public async Task<bool> DeleteRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var run = await runs.GetAsync(runId, cancellationToken);
        return run is not null && user.Owns(run.TenantId) && await runs.DeleteAsync(runId, cancellationToken);
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

        try
        {
            var effective = await ResolveForUseAsync(role, binding, jobId, cancellationToken);
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

        try
        {
            var effective = await ResolveForUseAsync(role, binding, jobId, cancellationToken);
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

    /// <summary>Crea una carpeta desde el explorador (solo proveedores que lo permiten, como la carpeta local).</summary>
    public async Task<CreateFolderResult> CreateFolderAsync(
        ProviderRole role, ProviderBinding binding, Guid? jobId, string parentPath, string name, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (registry.Find(role, binding.ProviderKey) is not IFolderCreator creator)
        {
            return new(null, "Este proveedor no permite crear carpetas.");
        }

        try
        {
            var effective = await ResolveForUseAsync(role, binding, jobId, cancellationToken);
            return new(await creator.CreateFolderAsync(new ProviderSettings(effective.Settings), parentPath, name, cancellationToken), null);
        }
#pragma warning disable CA1031 // El mensaje de error se muestra al usuario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new(null, ex.Message);
        }
    }

    /// <summary>Consulta al servidor los valores posibles de un campo (bases de datos, esquemas...) con la configuración en edición.</summary>
    public async Task<OptionsResult> ListOptionsAsync(
        ProviderRole role, ProviderBinding binding, Guid? jobId, string fieldKey, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (registry.Find(role, binding.ProviderKey) is not IOptionLister lister)
        {
            return OptionsResult.Fail("Este proveedor no permite listar valores.");
        }

        try
        {
            var effective = await ResolveForUseAsync(role, binding, jobId, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return OptionsResult.Ok(await lister.ListOptionsAsync(fieldKey, new ProviderSettings(effective.Settings), timeout.Token));
        }
#pragma warning disable CA1031 // El mensaje de error se muestra al usuario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return OptionsResult.Fail(ex is OperationCanceledException ? "Tiempo de espera agotado." : ex.Message);
        }
    }

    /// <summary>
    /// Guarda como conexión reutilizable los datos de acceso que el usuario configuró en el asistente (con los
    /// secretos ya guardados en el trabajo si los dejó vacíos).
    /// </summary>
    public async Task<SaveConnectionResult> SaveAsConnectionAsync(
        ProviderRole role, ProviderBinding binding, Guid? jobId, string name, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var effective = await WithStoredSecretsAsync(role, binding, jobId, cancellationToken);
        return await connections.CreateFromAsync(binding.ProviderKey, name, effective.Settings, cancellationToken);
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

    /// <summary>Configuración lista para usar: secretos guardados del trabajo y datos de la conexión elegida.</summary>
    private async Task<ProviderBinding> ResolveForUseAsync(
        ProviderRole role, ProviderBinding binding, Guid? jobId, CancellationToken cancellationToken)
    {
        var effective = await WithStoredSecretsAsync(role, binding, jobId, cancellationToken);
        return await connectionResolver.ResolveAsync(effective, user.TenantId, cancellationToken);
    }

    private void StripConnectionFields(ProviderRole role, ProviderBinding binding)
    {
        if (binding.ConnectionId is null || registry.Find(role, binding.ProviderKey) is not { } provider)
        {
            binding.ConnectionId = null;
            return;
        }

        foreach (var field in provider.Descriptor.ConnectionFields)
        {
            binding.Settings.Remove(field.Key);
        }
    }

    /// <summary>Quita el destino de restauración si no tiene tipo, y los campos que ese tipo no usa.</summary>
    private void CleanRestoreTarget(BackupJob job)
    {
        if (job.RestoreTarget is not { } target || registry.FindRestoreTarget(target.ProviderKey) is not { } provider)
        {
            job.RestoreTarget = null;
            return;
        }

        var known = provider.RestoreFields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in target.Settings.Keys.Where(k => !known.Contains(k)).ToList())
        {
            target.Settings.Remove(key);
        }
    }

    /// <summary>El destino de restauración es opcional, pero si se define debe estar completo y admitir el respaldo del origen.</summary>
    private async Task<IReadOnlyList<ValidationError>> ValidateRestoreTargetAsync(BackupJob job, CancellationToken cancellationToken)
    {
        if (job.RestoreTarget is not { } target || registry.FindRestoreTarget(target.ProviderKey) is not { } provider)
        {
            return [];
        }

        var extension = (registry.Find(ProviderRole.Source, job.Source.ProviderKey) as IBackupSource)?.ArtifactExtension(new ProviderSettings(job.Source.Settings));
        if (extension is not null && !provider.CanRestore("respaldo" + extension))
        {
            return [new ValidationError("Restore", $"{provider.Descriptor.DisplayName} no puede restaurar respaldos {extension}.")];
        }

        ProviderBinding resolved;
        try
        {
            resolved = await connectionResolver.ResolveAsync(target, user.TenantId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return [new ValidationError("Restore", ex.Message)];
        }

        return [.. provider.RestoreFields
            .Where(f => f.Required && string.IsNullOrWhiteSpace(resolved.Settings.GetValueOrDefault(f.Key)))
            .Select(f => new ValidationError($"Restore.{f.Key}", $"{f.Label} es obligatorio."))];
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
