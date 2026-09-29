using Microsoft.AspNetCore.Identity;

namespace Backup.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    /// <summary>Tenant al que pertenece (para un SuperAdmin, el que tiene seleccionado).</summary>
    public Guid TenantId { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public static class AppClaims
{
    public const string TenantId = "backup:tenant";
    public const string DisplayName = "backup:name";
}
