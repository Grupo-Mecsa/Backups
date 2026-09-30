namespace Backup.Domain.Jobs;

/// <summary>Vincula un trabajo con un proveedor (origen o destino) y su configuración.</summary>
public sealed class ProviderBinding
{
    /// <summary>Identificador del proveedor, p. ej. "postgres", "s3", "sftp".</summary>
    public string ProviderKey { get; set; } = string.Empty;

    public Dictionary<string, string?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Conexión guardada que aporta los datos de acceso. Null = el trabajo guarda toda la configuración.
    /// Con conexión, <see cref="Settings"/> solo contiene los campos propios del trabajo.
    /// </summary>
    public Guid? ConnectionId { get; set; }

    public ProviderBinding Clone() => new()
    {
        ProviderKey = ProviderKey,
        ConnectionId = ConnectionId,
        Settings = new Dictionary<string, string?>(Settings, StringComparer.OrdinalIgnoreCase),
    };
}
