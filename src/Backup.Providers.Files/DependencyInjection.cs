using Backup.Application;
using Backup.Providers.Files.AzureFiles;
using Backup.Providers.Files.Dropbox;
using Backup.Providers.Files.Ftp;
using Backup.Providers.Files.GoogleDrive;
using Backup.Providers.Files.Local;
using Backup.Providers.Files.OneDrive;
using Backup.Providers.Files.Sftp;
using Backup.Providers.Files.Smb;
using Backup.Providers.Files.WebDav;
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
        .AddBackupDestination<SmbDestination>()
        .AddBackupSource<WebDavSource>()
        .AddBackupDestination<WebDavDestination>()
        .AddBackupSource<AzureFilesSource>()
        .AddBackupDestination<AzureFilesDestination>()
        .AddBackupSource<GoogleDriveSource>()
        .AddBackupDestination<GoogleDriveDestination>()
        .AddBackupSource<OneDriveSource>()
        .AddBackupDestination<OneDriveDestination>()
        .AddBackupSource<DropboxSource>()
        .AddBackupDestination<DropboxDestination>();
}
