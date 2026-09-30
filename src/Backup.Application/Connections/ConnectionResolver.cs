using Backup.Application.Abstractions;
using Backup.Application.Providers;
using Backup.Domain.Jobs;

namespace Backup.Application.Connections;

/// <summary>
/// Combina un trabajo con sus conexiones guardadas: los datos de acceso salen de la conexión y el resto del
/// trabajo. Se aplica justo antes de usar la configuración (ejecutar, probar, explorar, validar), así un cambio en
/// la conexión alcanza a todos los trabajos que la usan.
/// </summary>
public sealed class ConnectionResolver(IConnectionRepository connections, IProviderRegistry registry)
{
    public async Task<BackupJob> ResolveAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        var resolved = Jobs.JobSecrets.Clone(job);
        resolved.Source = await ResolveAsync(resolved.Source, job.TenantId, cancellationToken);
        resolved.Destination = await ResolveAsync(resolved.Destination, job.TenantId, cancellationToken);
        return resolved;
    }

    /// <exception cref="InvalidOperationException">La conexión ya no existe o no corresponde al proveedor.</exception>
    public async Task<ProviderBinding> ResolveAsync(ProviderBinding binding, Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (binding.ConnectionId is not { } id)
        {
            return binding;
        }

        var connection = await connections.GetAsync(id, cancellationToken);
        if (connection is null || connection.TenantId != tenantId
            || !string.Equals(connection.ProviderKey, binding.ProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La conexión guardada que usa este trabajo ya no existe. Elige otra o configúrala de nuevo.");
        }

        var resolved = binding.Clone();
        var fields = registry.FindAny(binding.ProviderKey)?.Descriptor.ConnectionFields ?? [];
        foreach (var field in fields)
        {
            resolved.Settings[field.Key] = connection.Settings.GetValueOrDefault(field.Key);
        }

        return resolved;
    }
}
