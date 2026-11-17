using Backup.Application.Abstractions;
using Backup.Application.Jobs;
using Backup.Application.Pipeline;
using Backup.Application.Providers;
using Backup.Application.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddBackupApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<ArtifactPackager>();
        services.AddSingleton<JobSecrets>();
        services.AddSingleton<IJobValidator, JobValidator>();
        services.AddSingleton<IBackupRunner, BackupRunner>();
        services.AddScoped<JobService>();
        services.AddSingleton<Connections.ConnectionResolver>();
        services.AddScoped<Connections.ConnectionService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ArtifactService>();
        services.AddSingleton<RestoreRunner>();
        services.AddScoped<RestoreService>();
        services.AddSingleton<Notifications.SmtpResolver>();
        services.AddScoped<Notifications.NotificationService>();
        services.AddScoped<Tenants.TenantService>();
        services.AddSingleton<IRunNotifier, Notifications.EmailRunNotifier>();
        services.AddSingleton<Telegram.TelegramLinkCodes>();
        services.AddSingleton<Telegram.TelegramBotCommands>();
        services.AddSingleton<IRunNotifier, Telegram.TelegramRunNotifier>();
        services.AddSingleton<IRunNotifier, WhatsApp.WhatsAppRunNotifier>();
        services.AddScoped<Telegram.TelegramService>();
        return services;
    }

    public static IServiceCollection AddBackupSource<TSource>(this IServiceCollection services)
        where TSource : class, IBackupSource
    {
        services.AddSingleton<IBackupSource, TSource>();
        return services;
    }

    /// <summary>Registra un origen construido a mano (p. ej. una variante de un proveedor genérico).</summary>
    public static IServiceCollection AddBackupSource(this IServiceCollection services, Func<IServiceProvider, IBackupSource> factory)
    {
        services.AddSingleton(factory);
        return services;
    }

    /// <summary>Registra un destino construido a mano (p. ej. una variante de un proveedor genérico).</summary>
    public static IServiceCollection AddBackupDestination(this IServiceCollection services, Func<IServiceProvider, IBackupDestination> factory)
    {
        services.AddSingleton(factory);
        return services;
    }

    public static IServiceCollection AddBackupDestination<TDestination>(this IServiceCollection services)
        where TDestination : class, IBackupDestination
    {
        services.AddSingleton<IBackupDestination, TDestination>();
        return services;
    }
}
