namespace Backup.Domain.Connections;

/// <summary>
/// Conexión reutilizable (banco de credenciales): servidor, usuario, contraseña y demás datos de acceso de un
/// proveedor. Los trabajos la referencian y solo guardan lo propio (carpeta, base de datos, selección...).
/// </summary>
public sealed class Connection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Proveedor al que pertenece, p. ej. "ftp", "postgres", "s3". Sirve tanto para origen como para destino.</summary>
    public string ProviderKey { get; set; } = string.Empty;

    public Dictionary<string, string?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Connection Clone() => new()
    {
        Id = Id,
        TenantId = TenantId,
        Name = Name,
        ProviderKey = ProviderKey,
        Settings = new Dictionary<string, string?>(Settings, StringComparer.OrdinalIgnoreCase),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
