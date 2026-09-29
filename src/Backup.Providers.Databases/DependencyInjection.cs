using Backup.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Providers.Databases;

public static class DependencyInjection
{
    public static IServiceCollection AddDatabaseProviders(this IServiceCollection services) => services
        .AddBackupSource<SqlServerSource>()
        .AddBackupSource<PostgreSqlSource>()
        .AddBackupSource<MySqlSource>()
        .AddBackupSource<MongoDbSource>()
        .AddBackupSource<SqliteSource>();
}
