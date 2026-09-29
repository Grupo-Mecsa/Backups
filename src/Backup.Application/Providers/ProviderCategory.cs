namespace Backup.Application.Providers;

public enum ProviderCategory
{
    Database,
    Cloud,
    FileTransfer,
    Local,
}

public enum ProviderRole
{
    Source,
    Destination,
}
