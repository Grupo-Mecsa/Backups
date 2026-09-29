using System.Security.Claims;
using Backup.Application.Security;
using Backup.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;

namespace Backup.Web.Services;

/// <summary>
/// Usuario del circuito o petición actual, leído de los claims de la cookie.
/// En Blazor Server el estado de autenticación ya está resuelto al crear el circuito, por eso se lee de forma síncrona.
/// </summary>
public sealed class CurrentUser(AuthenticationStateProvider authentication) : ICurrentUser
{
    private ClaimsPrincipal? _principal;

    private ClaimsPrincipal Principal => _principal ??= Load();

    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;

    public string? UserId => Principal.FindFirstValue(ClaimTypes.NameIdentifier);

    public string? DisplayName => Principal.FindFirstValue(AppClaims.DisplayName) ?? Principal.Identity?.Name;

    public string? Email => Principal.FindFirstValue(ClaimTypes.Email) ?? Principal.Identity?.Name;

    public Guid TenantId => Guid.TryParse(Principal.FindFirstValue(AppClaims.TenantId), out var id) ? id : Guid.Empty;

    public string? Role => AppRoles.All.FirstOrDefault(Principal.IsInRole);

    public bool IsSuperAdmin => Role == AppRoles.SuperAdmin;

    public bool CanManage => Role is AppRoles.SuperAdmin or AppRoles.Admin;

    private ClaimsPrincipal Load()
    {
        try
        {
            return authentication.GetAuthenticationStateAsync().GetAwaiter().GetResult().User;
        }
        catch (InvalidOperationException)
        {
            // Fuera de una petición o circuito (no debería ocurrir en la UI).
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }
}
