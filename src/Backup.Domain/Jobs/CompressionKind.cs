namespace Backup.Domain.Jobs;

public enum CompressionKind
{
    None = 0,
    GZip = 1,
    Brotli = 2,
}
