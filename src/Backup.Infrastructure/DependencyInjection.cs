using Backup.Application;
using Backup.Application.Abstractions;
using Backup.Application.Notifications;
using Backup.Application.Pipeline;
using Backup.Application.Security;
using Backup.Application.Telegram;
using Backup.Infrastructure.Identity;
using Backup.Infrastructure.Notifications;
using Backup.Infrastructure.Persistence;
using Backup.Infrastructure.Processes;
using Backup.Infrastructure.Scheduling;
using Backup.Infrastructure.Security;
using Backup.Infrastructure.Telegram;
using Backup.Infrastructure.Transforms;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Infrastructure;

public static class DependencyInjection
{
    /// <param name="dataDirectory">Directorio persistente para la base SQLite y las llaves de cifrado.</param>
    public static IServiceCollection AddBackupInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);

        services.AddBackupApplication();
        services.Configure<BackupOptions>(configuration.GetSection(BackupOptions.SectionName));
        services.PostConfigure<BackupOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
            {
                options.WorkingDirectory = Path.Combine(Path.GetTempPath(), "backup-work");
            }
        });
        services.Configure<WebhookOptions>(configuration.GetSection(WebhookOptions.SectionName));
        services.Configure<PlatformSmtpOptions>(configuration.GetSection(PlatformSmtpOptions.SectionName));

        // Persistencia
        var connectionString = configuration.GetConnectionString("Backup")
            ?? $"Data Source={Path.Combine(dataDirectory, "backup.db")}";
        services.AddDbContextFactory<BackupDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IJobRepository, JobRepository>();
        services.AddSingleton<IConnectionRepository, ConnectionRepository>();
        services.AddSingleton<IRestoreRepository, RestoreRepository>();
        services.AddSingleton<IRunRepository, RunRepository>();
        services.AddSingleton<ITenantRepository, TenantRepository>();
        services.AddSingleton<INotificationSettingsRepository, NotificationSettingsRepository>();
        services.AddSingleton<ITelegramSubscriptionRepository, TelegramSubscriptionRepository>();

        // Identidad (la cookie de autenticación se configura en la capa web)
        services.Configure<BootstrapOptions>(configuration.GetSection(BootstrapOptions.SectionName));
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<BackupDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(48));
        services.AddScoped<IUserAdministration, UserAdministration>();
        services.AddScoped<PasswordRecovery>();
        services.Configure<RegistrationOptions>(configuration.GetSection(RegistrationOptions.SectionName));
        services.AddScoped<SelfRegistration>();

        // Secretos
        services.AddDataProtection()
            .SetApplicationName("Backup")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        // Pipeline
        services.AddSingleton<IStreamTransform, CompressionTransform>();
        services.AddSingleton<IStreamTransform, EncryptionTransform>();
        services.AddSingleton<IArtifactDecoder, ArtifactDecoder>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();

        // Planificación y ejecución
        services.AddSingleton<IScheduleCalculator, CronScheduleCalculator>();
        services.AddSingleton<ScheduleSignal>();
        services.AddSingleton<IScheduleSignal>(sp => sp.GetRequiredService<ScheduleSignal>());
        services.AddSingleton<BackupQueue>();
        services.AddSingleton<IBackupQueue>(sp => sp.GetRequiredService<BackupQueue>());
        services.AddHostedService<BackupWorker>();
        services.AddHostedService<SchedulerService>();

        // Notificaciones
        services.AddHttpClient(nameof(WebhookNotifier), c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<RunEvents>();
        services.AddSingleton<IRunNotifier>(sp => sp.GetRequiredService<RunEvents>());
        services.AddSingleton<IRunNotifier, WebhookNotifier>();
        services.AddSingleton<IEmailSender, MailKitEmailSender>();

        // Telegram: el token va en la URL, por eso el cliente HTTP no registra logs de peticiones.
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.AddHttpClient(TelegramBotClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(75)).RemoveAllLoggers();
        services.AddSingleton<TelegramBotClient>();
        services.AddSingleton<ITelegramBot>(sp => sp.GetRequiredService<TelegramBotClient>());
        services.AddHostedService<TelegramPollingService>();

        return services;
    }

    public static async Task MigrateBackupDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<BackupDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        await IdentitySeeder.SeedAsync(services, cancellationToken);
    }
}
