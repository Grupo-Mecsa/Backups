namespace Backup.Application.Providers;

/// <summary>
/// Servicio concreto (Supabase, Cloudflare R2, Azure Database...) construido sobre un proveedor genérico (PostgreSQL,
/// S3...): misma implementación, pero con su propia ficha, valores por defecto y ayudas pensadas para ese servicio.
/// Así agregar un servicio compatible es solo describirlo, sin duplicar la lógica del protocolo.
/// </summary>
/// <param name="Key">Clave propia (se guarda en trabajos y conexiones); no debe coincidir con la del proveedor base.</param>
public sealed record ProviderVariant(string Key, string DisplayName, string Description)
{
    /// <summary>Icono distinto al del proveedor base (null = el mismo).</summary>
    public string? Icon { get; init; }

    /// <summary>Ajusta cada campo del proveedor base (valores por defecto, ayudas...). Devolver null lo quita del formulario.</summary>
    public Func<SettingField, SettingField?>? Customize { get; init; }

    /// <summary>Campos propios del servicio, que van antes de los del proveedor base (p. ej. el ID de cuenta de R2).</summary>
    public IReadOnlyList<SettingField> ExtraFields { get; init; } = [];

    /// <summary>
    /// Traduce la configuración del servicio a la que entiende el proveedor base (p. ej. ID de cuenta → endpoint).
    /// Se aplica al ejecutar; lo guardado es siempre lo que escribió el usuario.
    /// </summary>
    public Func<ProviderSettings, ProviderSettings>? Normalize { get; init; }

    public ProviderDescriptor Apply(ProviderDescriptor baseDescriptor, string? description = null) => baseDescriptor with
    {
        Key = Key,
        DisplayName = DisplayName,
        Description = description ?? Description,
        Icon = Icon ?? baseDescriptor.Icon,
        Fields =
        [
            .. ExtraFields,
            .. baseDescriptor.Fields
                .Where(f => ExtraFields.All(e => !string.Equals(e.Key, f.Key, StringComparison.OrdinalIgnoreCase)))
                .Select(f => Customize is null ? f : Customize(f))
                .OfType<SettingField>(),
        ],
    };

    public ProviderSettings Apply(ProviderSettings settings) => Normalize?.Invoke(settings) ?? settings;
}
