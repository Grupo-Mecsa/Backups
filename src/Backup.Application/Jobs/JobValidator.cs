using Backup.Application.Abstractions;
using Backup.Application.Pipeline;
using Backup.Application.Providers;
using Backup.Domain.Jobs;

namespace Backup.Application.Jobs;

public sealed record ValidationError(string Field, string Message);

public interface IJobValidator
{
    IReadOnlyList<ValidationError> Validate(BackupJob job, IEnumerable<BackupJob> otherJobs);
}

public sealed class JobValidator(IProviderRegistry registry, IScheduleCalculator schedules) : IJobValidator
{
    public const int MinPassphraseLength = 8;

    public IReadOnlyList<ValidationError> Validate(BackupJob job, IEnumerable<BackupJob> otherJobs)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(job.Name))
        {
            errors.Add(new(nameof(job.Name), "El nombre es obligatorio."));
        }
        else if (job.Name.Length > 100)
        {
            errors.Add(new(nameof(job.Name), "El nombre no puede superar 100 caracteres."));
        }
        else
        {
            var slug = ArtifactNaming.Slug(job.Name);
            if (otherJobs.Any(o => o.Id != job.Id && ArtifactNaming.Slug(o.Name) == slug))
            {
                errors.Add(new(nameof(job.Name), "Ya existe otro trabajo con un nombre equivalente."));
            }
        }

        ValidateBinding(ProviderRole.Source, job.Source, errors);
        ValidateBinding(ProviderRole.Destination, job.Destination, errors);

        if (!string.IsNullOrWhiteSpace(job.Schedule) && !schedules.TryValidate(job.Schedule, job.TimeZone, out var cronError))
        {
            errors.Add(new(nameof(job.Schedule), cronError ?? "Expresión cron inválida."));
        }

        if (job.IsEncrypted && job.EncryptionPassphrase!.Length < MinPassphraseLength)
        {
            errors.Add(new(nameof(job.EncryptionPassphrase), $"La contraseña de cifrado debe tener al menos {MinPassphraseLength} caracteres."));
        }

        if (job.Retention.KeepLast < 0 || job.Retention.KeepDays < 0)
        {
            errors.Add(new(nameof(job.Retention), "Los valores de retención no pueden ser negativos."));
        }

        return errors;
    }

    private void ValidateBinding(ProviderRole role, ProviderBinding binding, List<ValidationError> errors)
    {
        var prefix = role == ProviderRole.Source ? "Source" : "Destination";
        var label = role == ProviderRole.Source ? "origen" : "destino";

        var provider = string.IsNullOrWhiteSpace(binding.ProviderKey) ? null : registry.Find(role, binding.ProviderKey);
        if (provider is null)
        {
            errors.Add(new(prefix, $"Selecciona un tipo de {label}."));
            return;
        }

        foreach (var field in provider.Descriptor.Fields.Where(f => f.Required))
        {
            if (!binding.Settings.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new($"{prefix}.{field.Key}", $"{field.Label} es obligatorio."));
            }
        }
    }
}
