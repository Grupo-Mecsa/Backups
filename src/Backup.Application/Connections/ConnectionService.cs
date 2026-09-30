using Backup.Application.Abstractions;
using Backup.Application.Jobs;
using Backup.Application.Providers;
using Backup.Application.Security;
using Backup.Domain.Connections;

namespace Backup.Application.Connections;

/// <param name="JobCount">Trabajos del tenant que usan la conexión (como origen o destino).</param>
public sealed record ConnectionSummary(Connection Connection, string ProviderName, string ProviderIcon, int JobCount);

/// <summary>Conexión lista para editar: sin secretos, pero indicando cuáles hay guardados.</summary>
public sealed record ConnectionDraft(Connection Connection, bool IsNew, IReadOnlySet<string> StoredSecrets)
{
    public bool HasStoredSecret(string field) => StoredSecrets.Contains(field);
}

public sealed record SaveConnectionResult(bool Success, IReadOnlyList<ValidationError> Errors, Guid ConnectionId);

/// <summary>
/// Banco de conexiones del tenant: datos de acceso reutilizables entre trabajos. Los secretos nunca vuelven a la UI;
/// al guardar, un secreto vacío significa "conservar el guardado".
/// </summary>
public sealed class ConnectionService(
    IConnectionRepository repository,
    IJobRepository jobs,
    IProviderRegistry registry,
    ICurrentUser user,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var all = await repository.ListAsync(user.TenantId, cancellationToken);
        var tenantJobs = await jobs.ListAsync(user.TenantId, cancellationToken);
        return [.. all
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c =>
            {
                var descriptor = registry.FindAny(c.ProviderKey)?.Descriptor;
                return new ConnectionSummary(
                    Strip(c).Connection,
                    descriptor?.DisplayName ?? c.ProviderKey,
                    descriptor?.Icon ?? "plug",
                    tenantJobs.Count(j => j.Source.ConnectionId == c.Id || j.Destination.ConnectionId == c.Id || j.RestoreTarget?.ConnectionId == c.Id));
            })];
    }

    /// <summary>Conexiones de un proveedor, para elegir una en el asistente de trabajos.</summary>
    public async Task<IReadOnlyList<Connection>> ListForProviderAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        return [.. (await repository.ListAsync(user.TenantId, cancellationToken))
            .Where(c => string.Equals(c.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => Strip(c).Connection)];
    }

    public ConnectionDraft NewDraft(string? providerKey = null)
    {
        user.EnsureCanManage();
        var connection = new Connection { TenantId = user.TenantId, ProviderKey = providerKey ?? string.Empty };
        ApplyDefaults(connection);
        return new ConnectionDraft(connection, IsNew: true, new HashSet<string>());
    }

    public async Task<ConnectionDraft?> GetForEditAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await GetOwnedAsync(id, cancellationToken);
        if (connection is null)
        {
            return null;
        }

        var (stripped, stored) = Strip(connection);
        return new ConnectionDraft(stripped, IsNew: false, stored);
    }

    /// <summary>Rellena los valores por defecto del proveedor (al elegirlo en una conexión nueva).</summary>
    public void ApplyDefaults(Connection connection)
    {
        connection.Settings.Clear();
        foreach (var field in Fields(connection.ProviderKey).Where(f => f.DefaultValue is not null))
        {
            connection.Settings[field.Key] = field.DefaultValue;
        }
    }

    public async Task<SaveConnectionResult> SaveAsync(Connection edited, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        var connection = edited.Clone();
        connection.TenantId = user.TenantId;
        connection.Name = connection.Name.Trim();

        var fields = Fields(connection.ProviderKey).ToList();
        var known = fields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in connection.Settings.Keys.Where(k => !known.Contains(k)).ToList())
        {
            connection.Settings.Remove(key);
        }

        var existing = await repository.GetAsync(connection.Id, cancellationToken);
        if (existing is not null && existing.TenantId != user.TenantId)
        {
            throw new UnauthorizedAccessException("La conexión pertenece a otro tenant.");
        }

        if (existing is not null)
        {
            // El proveedor no cambia: los trabajos que la usan dependen de él.
            connection.ProviderKey = existing.ProviderKey;
            connection.CreatedAt = existing.CreatedAt;
            foreach (var field in fields.Where(f => f.IsSecret))
            {
                if (string.IsNullOrEmpty(connection.Settings.GetValueOrDefault(field.Key)))
                {
                    connection.Settings[field.Key] = existing.Settings.GetValueOrDefault(field.Key);
                }
            }
        }

        var errors = Validate(connection, fields, await repository.ListAsync(user.TenantId, cancellationToken));
        if (errors.Count > 0)
        {
            return new SaveConnectionResult(false, errors, connection.Id);
        }

        connection.UpdatedAt = timeProvider.GetUtcNow();
        if (existing is null)
        {
            connection.CreatedAt = connection.UpdatedAt;
            await repository.AddAsync(connection, cancellationToken);
        }
        else
        {
            await repository.UpdateAsync(connection, cancellationToken);
        }

        return new SaveConnectionResult(true, [], connection.Id);
    }

    /// <returns>Null si se eliminó; si no, el motivo (p. ej. trabajos que aún la usan).</returns>
    public async Task<string?> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (await GetOwnedAsync(id, cancellationToken) is null)
        {
            return null;
        }

        var users = (await jobs.ListAsync(user.TenantId, cancellationToken))
            .Where(j => j.Source.ConnectionId == id || j.Destination.ConnectionId == id || j.RestoreTarget?.ConnectionId == id)
            .Select(j => j.Name)
            .ToList();
        if (users.Count > 0)
        {
            return $"La usan {users.Count} trabajo(s): {string.Join(", ", users)}. Cámbialos a otra conexión antes de eliminarla.";
        }

        await repository.DeleteAsync(id, cancellationToken);
        return null;
    }

    /// <summary>
    /// Prueba la conexión con lo que hay en pantalla (secretos vacíos = los guardados). Los campos propios de un
    /// trabajo toman su valor por defecto; si el proveedor exige alguno sin valor, se indica que se pruebe desde un trabajo.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(Connection edited, CancellationToken cancellationToken = default)
    {
        user.EnsureCanManage();
        if (registry.FindAny(edited.ProviderKey) is not IConnectionTester tester)
        {
            return new(false, "Este proveedor no permite probar la conexión.");
        }

        var settings = new Dictionary<string, string?>(edited.Settings, StringComparer.OrdinalIgnoreCase);
        if (await GetOwnedAsync(edited.Id, cancellationToken) is { } stored)
        {
            foreach (var field in Fields(stored.ProviderKey).Where(f => f.IsSecret && string.IsNullOrEmpty(settings.GetValueOrDefault(f.Key))))
            {
                settings[field.Key] = stored.Settings.GetValueOrDefault(field.Key);
            }
        }

        var descriptor = registry.FindAny(edited.ProviderKey)!.Descriptor;
        foreach (var field in descriptor.Fields.Where(f => !f.IsConnection && f.DefaultValue is not null))
        {
            settings.TryAdd(field.Key, field.DefaultValue);
        }

        if (descriptor.Fields.FirstOrDefault(f => !f.IsConnection && f.Required && string.IsNullOrWhiteSpace(settings.GetValueOrDefault(f.Key))) is { } missing)
        {
            return new(false, $"Este proveedor necesita «{missing.Label}» para probar, y ese dato es de cada trabajo. Prueba la conexión desde el asistente del trabajo.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return new(true, await tester.TestConnectionAsync(new ProviderSettings(settings), timeout.Token));
        }
#pragma warning disable CA1031 // El mensaje de error se muestra al usuario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new(false, ex is OperationCanceledException ? "Tiempo de espera agotado." : ex.Message);
        }
    }

    /// <summary>Guarda como conexión los datos de acceso de <paramref name="settings"/> (ya con los secretos completos).</summary>
    internal async Task<SaveConnectionResult> CreateFromAsync(
        string providerKey, string name, IReadOnlyDictionary<string, string?> settings, CancellationToken cancellationToken)
    {
        var connection = new Connection { TenantId = user.TenantId, ProviderKey = providerKey, Name = name };
        foreach (var field in Fields(providerKey))
        {
            connection.Settings[field.Key] = settings.GetValueOrDefault(field.Key);
        }

        var result = await SaveAsync(connection, cancellationToken);
        return result.Success ? result : result with { Errors = [.. result.Errors.Select(e => e with { Field = "Connection." + e.Field })] };
    }

    private IEnumerable<SettingField> Fields(string providerKey) =>
        registry.FindAny(providerKey)?.Descriptor.ConnectionFields ?? [];

    private List<ValidationError> Validate(Connection connection, IReadOnlyList<SettingField> fields, IEnumerable<Connection> others)
    {
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(connection.Name))
        {
            errors.Add(new("Name", "El nombre es obligatorio."));
        }
        else if (connection.Name.Length > 100)
        {
            errors.Add(new("Name", "El nombre admite hasta 100 caracteres."));
        }
        else if (others.Any(o => o.Id != connection.Id && string.Equals(o.Name, connection.Name, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new("Name", "Ya existe una conexión con ese nombre."));
        }

        if (fields.Count == 0)
        {
            errors.Add(new("Provider", "Elige el tipo de conexión."));
        }

        foreach (var field in fields.Where(f => f.Required && string.IsNullOrWhiteSpace(connection.Settings.GetValueOrDefault(f.Key))))
        {
            errors.Add(new($"Settings.{field.Key}", $"{field.Label} es obligatorio."));
        }

        return errors;
    }

    private async Task<Connection?> GetOwnedAsync(Guid id, CancellationToken cancellationToken)
    {
        user.EnsureAuthenticated();
        var connection = await repository.GetAsync(id, cancellationToken);
        return connection is not null && user.Owns(connection.TenantId) ? connection : null;
    }

    private (Connection Connection, HashSet<string> Stored) Strip(Connection connection)
    {
        var copy = connection.Clone();
        var stored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields(copy.ProviderKey).Where(f => f.IsSecret))
        {
            if (!string.IsNullOrEmpty(copy.Settings.GetValueOrDefault(field.Key)))
            {
                stored.Add(field.Key);
            }

            copy.Settings.Remove(field.Key);
        }

        return (copy, stored);
    }
}
