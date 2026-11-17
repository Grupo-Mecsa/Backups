using Backup.Application;
using Backup.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Providers.Databases;

public static class DependencyInjection
{
    public static IServiceCollection AddDatabaseProviders(this IServiceCollection services) => services
        .AddBackupSource<SqlServerSource>()
        .AddBackupSource<AzureSqlSource>()
        .AddBackupSource<PostgreSqlSource>()
        .AddBackupSource(sp => new PostgreSqlSource(sp.GetRequiredService<IProcessRunner>(), DatabaseVariants.Supabase))
        .AddBackupSource(sp => new PostgreSqlSource(sp.GetRequiredService<IProcessRunner>(), DatabaseVariants.Neon))
        .AddBackupSource(sp => new PostgreSqlSource(sp.GetRequiredService<IProcessRunner>(), DatabaseVariants.AzurePostgreSql))
        .AddBackupSource<MySqlSource>()
        .AddBackupSource(sp => new MySqlSource(sp.GetRequiredService<IProcessRunner>(), DatabaseVariants.AzureMySql))
        .AddBackupSource<MongoDbSource>()
        .AddBackupSource(sp => new MongoDbSource(sp.GetRequiredService<IProcessRunner>(), DatabaseVariants.MongoAtlas))
        .AddBackupSource<SqliteSource>();
}
