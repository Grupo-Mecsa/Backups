namespace Backup.Domain.Tenants;

/// <summary>Organización aislada: sus trabajos, ejecuciones, usuarios y alertas no son visibles para otros tenants.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
