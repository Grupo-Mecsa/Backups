using Backup.Application.Abstractions;
using Backup.Application.Connections;
using Backup.Application.Jobs;
using Backup.Application.Providers;
using Backup.Application.Security;
using Backup.Domain.Jobs;
using Backup.Domain.Runs;

namespace Backup.Application.Runs;

public sealed record StartRestoreResult(bool Success, IReadOnlyList<ValidationError> Errors, Guid RestoreId);

/// <summary>Punto de partida de una restauración.</summary>
public enum RestorePreset
{
    /// <summary>Configurado a mano (desde cero o con una conexión guardada).</summary>
    Custom,

    /// <summary>El destino de restauración predeterminado del trabajo.</summary>
    JobTarget,

    /// <summary>El mismo origen del que se sacó el respaldo.</summary>
    Source,
}

/// <summary>De dónde completar los secretos que el usuario dejó vacíos ("sin cambios"): un trabajo y cuál de sus configuraciones.</summary>
public sealed record RestoreSecrets(Guid JobId, RestorePreset Preset)
{
    public static readonly RestoreSecrets None = new(Guid.Empty, RestorePreset.Custom);
}

/// <summary>Configuración lista para editar, sin secretos, indicando cuáles están guardados.</summary>
public sealed record RestoreDraft(RestorePreset Preset, ProviderBinding Binding, IReadOnlySet<string> StoredSecrets);

/// <summary>
/// Casos de uso de restauración para la UI: qué destinos admite un respaldo, puntos de partida (destino del
/// trabajo, origen), iniciar, seguir y cancelar restauraciones, y probar o explorar el destino. Todo limitado al
/// tenant y a administradores; los secretos guardados se reutilizan sin volver al navegador.
/// </summary>
public sealed class RestoreService(
    IRunRepository runs,
    IJobRepository jobs,
    IRestoreRepository restores,
    IProviderRegistry registry,
    ConnectionResolver connections,
    ArtifactService artifacts,
    RestoreRunner runner,
    ICurrentUser user,
    TimeProvider timeProvider)
{
    /// <summary>Ejecución con respaldo del tenant actual, o null.</summary>
    public async Task<BackupRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var run = await runs.GetAsync(runId, cancellationToken);
        return run is not null && user.Owns(run.TenantId) && ArtifactService.HasArtifact(run) ? run : null;
    }

    /// <summary>Destinos que admiten este respaldo (según su tipo ya descifrado y descomprimido).</summary>
    public IReadOnlyList<IRestoreTarget> TargetsFor(BackupRun run) => [.. registry.RestoreTargetsFor(artifacts.DecodedName(run))];

    /// <summary>Destinos que admitirán el respaldo de este origen (para el destino predeterminado del trabajo).</summary>
    public IReadOnlyList<IRestoreTarget> TargetsForSource(ProviderBinding source)
    {
        var extension = (registry.Find(ProviderRole.Source, source.ProviderKey) as IBackupSource)?.ArtifactExtension(new ProviderSettings(source.Settings));
        return extension is null
            ? [.. registry.Sources.Cast<IProvider>().Concat(registry.Destinations).OfType<IRestoreTarget>().DistinctBy(t => t.Descriptor.Key)]
            : [.. registry.RestoreTargetsFor("respaldo" + extension)];
    }

    /// <summary>Puntos de partida disponibles para restaurar esta ejecución: destino del trabajo y origen, si admiten el respaldo.</summary>
    public async Task<IReadOnlyList<RestoreDraft>> PresetsAsync(BackupRun run, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (await GetOwnedJobAsync(run.JobId, cancellationToken) is not { } job)
        {
            return [];
        }

        var name = artifacts.DecodedName(run);
        var drafts = new List<RestoreDraft>();
        foreach (var (preset, stored) in new[] { (RestorePreset.JobTarget, job.RestoreTarget), (RestorePreset.Source, job.Source) })
        {
            if (stored is not null && registry.FindRestoreTarget(stored.ProviderKey) is { } provider && provider.CanRestore(name))
            {
                drafts.Add(Draft(preset, stored, provider));
            }
        }

        return drafts;
    }

    public async Task<StartRestoreResult> StartAsync(Guid runId, ProviderBinding target, RestoreSecrets secrets, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var run = await GetRunAsync(runId, cancellationToken) ?? throw new KeyNotFoundException("Ejecución no encontrada.");
        var provider = registry.FindRestoreTarget(target.ProviderKey);
        if (provider is null || !provider.CanRestore(artifacts.DecodedName(run)))
        {
            return Fail(new ValidationError("Target", "Elige a dónde restaurar."));
        }

        ProviderBinding resolved;
        try
        {
            resolved = await EffectiveAsync(target, secrets, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(new ValidationError("Target", ex.Message));
        }

        var errors = provider.RestoreFields
            .Where(f => f.Required && string.IsNullOrWhiteSpace(resolved.Settings.GetValueOrDefault(f.Key)))
            .Select(f => new ValidationError($"Target.{f.Key}", $"{f.Label} es obligatorio."))
            .ToList();
        if (errors.Count > 0)
        {
            return new StartRestoreResult(false, errors, Guid.Empty);
        }

        var settings = new ProviderSettings(resolved.Settings);
        var restore = new RestoreOperation
        {
            TenantId = user.TenantId,
            RunId = run.Id,
            JobName = run.JobName,
            ArtifactName = run.ArtifactName!,
            TargetProvider = provider.Descriptor.Key,
            TargetSummary = $"{provider.Descriptor.DisplayName} · {provider.DescribeTarget(settings)}"
                + (secrets.Preset == RestorePreset.Source ? " (origen)" : string.Empty),
            RequestedBy = user.DisplayName,
            StartedAt = timeProvider.GetUtcNow(),
        };
        await restores.AddAsync(restore, cancellationToken);
        runner.Start(restore, resolved);
        return new StartRestoreResult(true, [], restore.Id);
    }

    public async Task<IReadOnlyList<RestoreOperation>> ListAsync(CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        return await restores.ListAsync(user.TenantId, cancellationToken: cancellationToken);
    }

    public async Task<RestoreOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var restore = await restores.GetAsync(id, cancellationToken);
        return restore is not null && user.Owns(restore.TenantId) ? restore : null;
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetAsync(id, cancellationToken) is not null && runner.Cancel(id);

    public async Task<ConnectionTestResult> TestAsync(ProviderBinding target, RestoreSecrets secrets, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (registry.FindRestoreTarget(target.ProviderKey) is not IConnectionTester tester)
        {
            return new(false, "Este destino no permite probar la conexión.");
        }

        return await GuardAsync(
            async token => new ConnectionTestResult(true, await tester.TestConnectionAsync(await SettingsAsync(target, secrets, token), token)),
            message => new ConnectionTestResult(false, message),
            cancellationToken);
    }

    public async Task<BrowseResult> BrowseAsync(ProviderBinding target, RestoreSecrets secrets, string? path, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (registry.FindRestoreTarget(target.ProviderKey) is not IFolderBrowser browser)
        {
            return BrowseResult.Fail("Este destino no permite explorar carpetas.");
        }

        return await GuardAsync(
            async token => BrowseResult.Ok(await browser.BrowseAsync(await SettingsAsync(target, secrets, token), path, token)),
            BrowseResult.Fail,
            cancellationToken);
    }

    public async Task<OptionsResult> ListOptionsAsync(ProviderBinding target, RestoreSecrets secrets, string fieldKey, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (registry.FindRestoreTarget(target.ProviderKey) is not IOptionLister lister)
        {
            return OptionsResult.Fail("Este destino no permite listar valores.");
        }

        return await GuardAsync(
            async token => OptionsResult.Ok(await lister.ListOptionsAsync(fieldKey, await SettingsAsync(target, secrets, token), token)),
            OptionsResult.Fail,
            cancellationToken);
    }

    public async Task<CreateFolderResult> CreateFolderAsync(
        ProviderBinding target, RestoreSecrets secrets, string parentPath, string name, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (registry.FindRestoreTarget(target.ProviderKey) is not IFolderCreator creator)
        {
            return new(null, "Este destino no permite crear carpetas.");
        }

        return await GuardAsync(
            async token => new CreateFolderResult(await creator.CreateFolderAsync(await SettingsAsync(target, secrets, token), parentPath, name, token), null),
            message => new CreateFolderResult(null, message),
            cancellationToken);
    }

    /// <summary>
    /// Copia editable de una configuración guardada: solo los campos de restauración (con sus valores por defecto
    /// si faltan) y sin secretos. La conexión elegida se conserva.
    /// </summary>
    private static RestoreDraft Draft(RestorePreset preset, ProviderBinding stored, IRestoreTarget provider)
    {
        var binding = new ProviderBinding { ProviderKey = provider.Descriptor.Key, ConnectionId = stored.ConnectionId };
        var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in provider.RestoreFields)
        {
            var value = stored.Settings.TryGetValue(field.Key, out var existing) ? existing : field.DefaultValue;
            if (field.IsSecret)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    secrets.Add(field.Key);
                }

                continue;
            }

            binding.Settings[field.Key] = value;
        }

        return new RestoreDraft(preset, binding, secrets);
    }

    /// <summary>Configuración lista para usar: secretos guardados donde se dejaron vacíos y datos de la conexión elegida.</summary>
    private async Task<ProviderBinding> EffectiveAsync(ProviderBinding target, RestoreSecrets secrets, CancellationToken cancellationToken)
    {
        var effective = target.Clone();
        if (secrets.Preset != RestorePreset.Custom
            && await GetOwnedJobAsync(secrets.JobId, cancellationToken) is { } job
            && (secrets.Preset == RestorePreset.Source ? job.Source : job.RestoreTarget) is { } stored
            && string.Equals(stored.ProviderKey, target.ProviderKey, StringComparison.OrdinalIgnoreCase)
            && registry.FindRestoreTarget(target.ProviderKey) is { } provider)
        {
            foreach (var field in provider.RestoreFields.Where(f => f.IsSecret && string.IsNullOrEmpty(effective.Settings.GetValueOrDefault(f.Key))))
            {
                effective.Settings[field.Key] = stored.Settings.GetValueOrDefault(field.Key);
            }
        }

        return await connections.ResolveAsync(effective, user.TenantId, cancellationToken);
    }

    private async Task<BackupJob?> GetOwnedJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty)
        {
            return null;
        }

        var job = await jobs.GetAsync(jobId, cancellationToken);
        return job is not null && user.Owns(job.TenantId) ? job : null;
    }

    private static StartRestoreResult Fail(ValidationError error) => new(false, [error], Guid.Empty);

    private async Task<ProviderSettings> SettingsAsync(ProviderBinding target, RestoreSecrets secrets, CancellationToken cancellationToken) =>
        new((await EffectiveAsync(target, secrets, cancellationToken)).Settings);

    private static async Task<T> GuardAsync<T>(Func<CancellationToken, Task<T>> action, Func<string, T> fail, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await action(timeout.Token);
        }
#pragma warning disable CA1031 // El mensaje de error se muestra al usuario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return fail(ex is OperationCanceledException ? "Tiempo de espera agotado." : ex.Message);
        }
    }
}
