namespace Backup.Domain.Jobs;

/// <summary>Vincula un trabajo con un proveedor (origen o destino) y su configuración.</summary>
public sealed class ProviderBinding
{
    /// <summary>Identificador del proveedor, p. ej. "postgres", "s3", "sftp".</summary>
    public string ProviderKey { get; set; } = string.Empty;

    public Dictionary<string, string?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ProviderBinding Clone() => new()
    {
        ProviderKey = ProviderKey,
        Settings = new Dictionary<string, string?>(Settings, StringComparer.OrdinalIgnoreCase),
    };
}
