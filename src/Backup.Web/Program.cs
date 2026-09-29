using Backup.Application.Security;
using Backup.Infrastructure;
using Backup.Providers.Cloud;
using Backup.Providers.Databases;
using Backup.Providers.Files;
using Backup.Web.Components;
using Backup.Web.Services;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = builder.Configuration["Backup:DataDirectory"] is { Length: > 0 } configured
    ? configured
    : Path.Combine(builder.Environment.ContentRootPath, "data");

builder.Services
    .AddBackupInfrastructure(builder.Configuration, dataDirectory)
    .AddDatabaseProviders()
    .AddCloudProviders()
    .AddFileProviders();

// Autenticación con cookie de Identity y autorización por roles.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies(cookies => cookies.ApplicationCookie?.Configure(options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Cookie.Name = "backuphub.auth";
    }));
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AppPolicies.CanManage, policy => policy.RequireRole(AppRoles.SuperAdmin, AppRoles.Admin))
    .AddPolicy(AppPolicies.SuperAdmin, policy => policy.RequireRole(AppRoles.SuperAdmin));
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
builder.Services.AddScoped<IAccountLinks, AccountLinks>();

builder.Services.AddScoped<ToastService>();
builder.Services.AddHealthChecks();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

Display.Configure(app.Configuration["Backup:DisplayTimeZone"]);
await app.Services.MigrateBackupDatabaseAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapAccountEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
