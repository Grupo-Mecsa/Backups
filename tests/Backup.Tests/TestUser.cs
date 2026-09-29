using Backup.Application.Security;

namespace Backup.Tests;

/// <summary>Usuario simulado para ejercitar los servicios de aplicación sin la capa web.</summary>
public sealed class TestUser : ICurrentUser
{
    public bool IsAuthenticated { get; set; } = true;
    public string? UserId { get; set; } = "test-user";
    public string? DisplayName { get; set; } = "Pruebas";
    public Guid TenantId { get; set; }
    public string? Role { get; set; } = AppRoles.Admin;
}
