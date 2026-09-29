namespace Backup.Domain.Tenants;

/// <summary>Organización aislada: sus trabajos, ejecuciones, usuarios y alertas no son visibles para otros tenants.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>Creado por autorregistro y aún sin aprobar por un super administrador.</summary>
    public bool PendingApproval { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
