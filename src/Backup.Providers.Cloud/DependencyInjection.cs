using Backup.Application;
using Backup.Providers.Cloud.Azure;
using Backup.Providers.Cloud.S3;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Providers.Cloud;

public static class DependencyInjection
{
    public static IServiceCollection AddCloudProviders(this IServiceCollection services)
    {
        services
            .AddBackupSource<S3Source>()
            .AddBackupDestination<S3Destination>()
            .AddBackupSource<AzureBlobSource>()
            .AddBackupDestination<AzureBlobDestination>();

        foreach (var variant in S3Variants.All)
        {
            services
                .AddBackupSource(_ => new S3Source(variant))
                .AddBackupDestination(_ => new S3Destination(variant));
        }

        return services;
    }
}
