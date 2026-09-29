using Backup.Application.Security;
using Backup.Domain.Tenants;
using Backup.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.Infrastructure.Identity;

public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    public string TenantName { get; set; } = "Principal";
    public string AdminEmail { get; set; } = "admin@backup.local";

    /// <summary>Contraseña del SuperAdmin inicial. Si está vacía se genera una y se escribe en el log.</summary>
    public string? AdminPassword { get; set; }
}

/// <summary>
/// Prepara la base en el primer arranque: roles, tenant principal y SuperAdmin inicial.
/// También asigna al tenant principal los trabajos creados antes de existir los tenants.
/// </summary>
public static partial class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentitySeeder));
        var options = provider.GetRequiredService<IOptions<BootstrapOptions>>().Value;
        var roles = provider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = provider.GetRequiredService<IDbContextFactory<BackupDbContext>>();

        foreach (var role in AppRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole(role));
            }
        }

        await using var context = await db.CreateDbContextAsync(cancellationToken);
        var tenant = await context.Tenants.OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant { Name = options.TenantName };
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Datos anteriores a la multi-tenencia.
        await context.Jobs.Where(j => j.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(j => j.TenantId, tenant.Id), cancellationToken);
        await context.Runs.Where(r => r.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(r => r.TenantId, tenant.Id), cancellationToken);

        if (await users.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var generated = string.IsNullOrWhiteSpace(options.AdminPassword);
        var password = generated ? GeneratePassword() : options.AdminPassword!;
        var admin = new ApplicationUser
        {
            UserName = options.AdminEmail,
            Email = options.AdminEmail,
            EmailConfirmed = true,
            DisplayName = "Administrador",
            TenantId = tenant.Id,
            LockoutEnabled = true,
        };

        var result = await users.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("No se pudo crear el administrador inicial: " +
                string.Join("; ", result.Errors.Select(IdentityMessages.Translate)));
        }

        await users.AddToRoleAsync(admin, AppRoles.SuperAdmin);
        if (generated)
        {
            LogGeneratedAdmin(logger, options.AdminEmail, password);
        }
        else
        {
            LogCreatedAdmin(logger, options.AdminEmail);
        }
    }

    /// <summary>Contraseña aleatoria que siempre cumple la política (mayúscula, minúscula y número).</summary>
    internal static string GeneratePassword()
    {
        static string Pick(string alphabet, int length) => System.Security.Cryptography.RandomNumberGenerator.GetString(alphabet, length);
        return Pick("ABCDEFGHJKLMNPQRSTUVWXYZ", 4) + Pick("abcdefghijkmnpqrstuvwxyz", 6) + Pick("23456789", 4) + "!";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Administrador inicial creado: {Email} / contraseña: {Password}  ← cámbiala tras iniciar sesión")]
    private static partial void LogGeneratedAdmin(ILogger logger, string email, string password);

    [LoggerMessage(Level = LogLevel.Information, Message = "Administrador inicial creado: {Email}")]
    private static partial void LogCreatedAdmin(ILogger logger, string email);
}
