using Backup.Application.Abstractions;
using Backup.Application.Security;
using Backup.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Backup.Web.Services;

/// <summary>
/// Acciones que modifican la cookie de sesión. Blazor interactivo no puede escribir cookies,
/// así que se exponen como POST normales (con antiforgery) y se redirige al terminar.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/account");

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return TypedResults.LocalRedirect("~/account/login");
        });

        group.MapPost("/switch-tenant", async (
            [FromForm] Guid tenantId,
            [FromForm] string? returnUrl,
            HttpContext http,
            UserManager<ApplicationUser> users,
            SignInManager<ApplicationUser> signIn,
            ITenantRepository tenants) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null || !await users.IsInRoleAsync(user, AppRoles.SuperAdmin) || await tenants.GetAsync(tenantId) is null)
            {
                return Results.Forbid();
            }

            user.TenantId = tenantId;
            await users.UpdateAsync(user);
            await signIn.RefreshSignInAsync(user);
            return Results.LocalRedirect(string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') ? "~/" : "~" + returnUrl);
        }).RequireAuthorization(AppPolicies.SuperAdmin);

        return endpoints;
    }
}
