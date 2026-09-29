namespace Backup.Application.Providers;

/// <summary>Metadatos de un proveedor de origen o destino.</summary>
/// <param name="Icon">Nombre de icono que la UI sabe dibujar (database, cloud, folder, server...).</param>
public sealed record ProviderDescriptor(
    string Key,
    string DisplayName,
    string Description,
    ProviderCategory Category,
    string Icon,
    IReadOnlyList<SettingField> Fields)
{
    public IEnumerable<SettingField> SecretFields => Fields.Where(f => f.IsSecret);
}
