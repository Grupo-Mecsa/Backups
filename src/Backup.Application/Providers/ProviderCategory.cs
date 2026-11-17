namespace Backup.Application.Providers;

public enum ProviderCategory
{
    Database,
    Cloud,

    /// <summary>Unidades de archivos en la nube con cuenta de usuario (Google Drive, OneDrive, Dropbox).</summary>
    CloudDrive,
    FileTransfer,
    Local,
}

public enum ProviderRole
{
    Source,
    Destination,
}
