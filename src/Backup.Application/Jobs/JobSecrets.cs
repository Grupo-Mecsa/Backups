using Backup.Application.Providers;
using Backup.Domain.Jobs;

namespace Backup.Application.Jobs;

/// <summary>
/// Reglas sobre los valores secretos de un trabajo: se quitan antes de mostrarse en la UI
/// y, al guardar, un secreto vacío significa "conservar el valor almacenado".
/// </summary>
public sealed class JobSecrets(IProviderRegistry registry)
{
    public const string EncryptionKey = "encryption";

    public static string Key(ProviderRole role, string field) =>
        $"{(role == ProviderRole.Source ? "source" : "destination")}:{field}";

    /// <summary>Clave de un secreto del destino de restauración predeterminado.</summary>
    public static string RestoreKey(string field) => $"restore:{field}";

    /// <summary>Enumera los campos secretos de un trabajo como (clave, binding, campo).</summary>
    public IEnumerable<(string Key, ProviderBinding Binding, string Field)> Enumerate(BackupJob job)
    {
        foreach (var (role, binding) in new[] { (ProviderRole.Source, job.Source), (ProviderRole.Destination, job.Destination) })
        {
            var provider = registry.Find(role, binding.ProviderKey);
            if (provider is null)
            {
                continue;
            }

            foreach (var field in provider.Descriptor.SecretFields)
            {
                yield return (Key(role, field.Key), binding, field.Key);
            }
        }

        if (job.RestoreTarget is { } restore && registry.FindRestoreTarget(restore.ProviderKey) is { } target)
        {
            foreach (var field in target.RestoreFields.Where(f => f.IsSecret))
            {
                yield return (RestoreKey(field.Key), restore, field.Key);
            }
        }
    }

    /// <summary>Copia del trabajo sin secretos, junto con las claves de los secretos que tenía guardados.</summary>
    public (BackupJob Job, HashSet<string> Stored) Strip(BackupJob job)
    {
        var copy = Clone(job);
        var stored = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, binding, field) in Enumerate(copy))
        {
            if (binding.Settings.TryGetValue(field, out var value) && !string.IsNullOrEmpty(value))
            {
                stored.Add(key);
            }

            binding.Settings.Remove(field);
        }

        if (copy.IsEncrypted)
        {
            stored.Add(EncryptionKey);
        }

        copy.EncryptionPassphrase = null;
        return (copy, stored);
    }

    /// <summary>Rellena en <paramref name="edited"/> los secretos vacíos con los valores de <paramref name="stored"/>.</summary>
    public void MergeFrom(BackupJob edited, BackupJob stored, bool keepEncryption)
    {
        foreach (var (_, binding, field) in Enumerate(edited))
        {
            if (binding.Settings.TryGetValue(field, out var value) && !string.IsNullOrEmpty(value))
            {
                continue;
            }

            var storedBinding = ReferenceEquals(binding, edited.Source) ? stored.Source
                : ReferenceEquals(binding, edited.Destination) ? stored.Destination
                : stored.RestoreTarget;
            if (storedBinding is not null
                && string.Equals(storedBinding.ProviderKey, binding.ProviderKey, StringComparison.OrdinalIgnoreCase)
                && storedBinding.Settings.TryGetValue(field, out var storedValue))
            {
                binding.Settings[field] = storedValue;
            }
        }

        if (keepEncryption && string.IsNullOrEmpty(edited.EncryptionPassphrase))
        {
            edited.EncryptionPassphrase = stored.EncryptionPassphrase;
        }
    }

    public static BackupJob Clone(BackupJob job) => new()
    {
        Id = job.Id,
        TenantId = job.TenantId,
        Name = job.Name,
        Description = job.Description,
        Enabled = job.Enabled,
        Source = job.Source.Clone(),
        Destination = job.Destination.Clone(),
        RestoreTarget = job.RestoreTarget?.Clone(),
        Schedule = job.Schedule,
        TimeZone = job.TimeZone,
        Compression = job.Compression,
        EncryptionPassphrase = job.EncryptionPassphrase,
        Retention = new RetentionPolicy { KeepLast = job.Retention.KeepLast, KeepDays = job.Retention.KeepDays },
        CreatedAt = job.CreatedAt,
        UpdatedAt = job.UpdatedAt,
    };
}
