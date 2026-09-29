namespace Backup.Application.Security;

public static class AppRoles
{
    /// <summary>Administra la plataforma: tenants y usuarios de cualquier tenant.</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>Administra trabajos, usuarios y alertas de su tenant.</summary>
    public const string Admin = "Admin";

    /// <summary>Solo consulta trabajos, historial y panel.</summary>
    public const string Reader = "Lector";

    public static readonly IReadOnlyList<string> All = [SuperAdmin, Admin, Reader];

    /// <summary>Roles que un Admin de tenant puede asignar.</summary>
    public static readonly IReadOnlyList<string> TenantAssignable = [Admin, Reader];

    public static string Label(string role) => role switch
    {
        SuperAdmin => "Super administrador",
        Admin => "Administrador",
        _ => "Lector",
    };
}

public static class AppPolicies
{
    public const string CanManage = "CanManage";
    public const string SuperAdmin = "SuperAdmin";
}

/// <summary>Usuario autenticado de la petición o circuito actual.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string? UserId { get; }
    string? DisplayName { get; }

    /// <summary>Tenant sobre el que opera el usuario (para un SuperAdmin, el que tiene seleccionado).</summary>
    Guid TenantId { get; }

    string? Role { get; }
    bool IsSuperAdmin => Role == AppRoles.SuperAdmin;
    bool CanManage => Role is AppRoles.SuperAdmin or AppRoles.Admin;
}

public static class CurrentUserExtensions
{
    public static void EnsureCanManage(this ICurrentUser user)
    {
        if (!user.IsAuthenticated || !user.CanManage)
        {
            throw new UnauthorizedAccessException("No tienes permisos para realizar esta acción.");
        }
    }

    public static void EnsureAuthenticated(this ICurrentUser user)
    {
        if (!user.IsAuthenticated)
        {
            throw new UnauthorizedAccessException("Debes iniciar sesión.");
        }
    }

    /// <summary>Verifica que un recurso pertenezca al tenant del usuario.</summary>
    public static bool Owns(this ICurrentUser user, Guid tenantId) => user.IsAuthenticated && user.TenantId == tenantId;
}
