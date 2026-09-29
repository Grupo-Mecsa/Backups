using System.IO.Compression;
using Backup.Application.Pipeline;
using Backup.Domain.Jobs;

namespace Backup.Infrastructure.Transforms;

public sealed class CompressionTransform : IStreamTransform
{
    public int Order => 10;

    public bool AppliesTo(BackupJob job) => job.Compression != CompressionKind.None;

    public string GetExtension(BackupJob job) => job.Compression switch
    {
        CompressionKind.GZip => ".gz",
        CompressionKind.Brotli => ".br",
        _ => string.Empty,
    };

    public Stream WrapWrite(Stream output, BackupJob job) => job.Compression switch
    {
        CompressionKind.GZip => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
        CompressionKind.Brotli => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
        _ => throw new NotSupportedException($"Compresión no soportada: {job.Compression}"),
    };
}
