using Backup.Application;
using Backup.Providers.Files.Ftp;
using Backup.Providers.Files.Local;
using Backup.Providers.Files.Sftp;
using Backup.Providers.Files.Smb;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Providers.Files;

public static class DependencyInjection
{
    public static IServiceCollection AddFileProviders(this IServiceCollection services) => services
        .AddBackupSource<LocalFolderSource>()
        .AddBackupDestination<LocalFolderDestination>()
        .AddBackupSource<FtpSource>()
        .AddBackupDestination<FtpDestination>()
        .AddBackupSource<SftpSource>()
        .AddBackupDestination<SftpDestination>()
        .AddBackupSource<SmbSource>()
        .AddBackupDestination<SmbDestination>();
}
