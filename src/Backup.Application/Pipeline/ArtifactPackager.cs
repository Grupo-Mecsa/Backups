using System.Security.Cryptography;
using Backup.Application.Providers;
using Backup.Domain.Jobs;

namespace Backup.Application.Pipeline;

public sealed record PackagedArtifact(string FilePath, string Extensions, long Size, string Sha256);

/// <summary>Aplica las transformaciones registradas (compresión, cifrado...) al artefacto de origen.</summary>
public sealed class ArtifactPackager(IEnumerable<IStreamTransform> transforms)
{
    private readonly IReadOnlyList<IStreamTransform> _transforms = [.. transforms.OrderBy(t => t.Order)];

    public async Task<PackagedArtifact> PackageAsync(
        BackupJob job, BackupArtifact artifact, IRunLog log, CancellationToken cancellationToken)
    {
        var active = _transforms.Where(t => t.AppliesTo(job)).ToList();
        var extensions = artifact.Extension + string.Concat(active.Select(t => t.GetExtension(job)));
        var outputPath = artifact.FilePath + ".pkg";

        var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await using (var hashing = new HashingStream(fileStream))
        {
            // Se envuelve en orden inverso: los datos pasan primero por la transformación de menor Order.
            Stream pipeline = hashing;
            var owned = new List<Stream>();
            for (var i = active.Count - 1; i >= 0; i--)
            {
                pipeline = active[i].WrapWrite(pipeline, job);
                owned.Add(pipeline);
            }

            await using (var input = new FileStream(artifact.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            {
                await input.CopyToAsync(pipeline, cancellationToken);
            }

            // Cerrar de afuera hacia adentro para vaciar bloques finales (gzip, AES, HMAC).
            for (var i = owned.Count - 1; i >= 0; i--)
            {
                await owned[i].DisposeAsync();
            }

            await hashing.FlushAsync(cancellationToken);
            if (active.Count > 0)
            {
                log.Info($"Transformaciones aplicadas: {string.Join(", ", active.Select(t => t.GetExtension(job)))}");
            }

            return new PackagedArtifact(outputPath, extensions, hashing.BytesWritten, hashing.GetHashHex());
        }
    }

    /// <summary>Stream de escritura que calcula SHA-256 y el tamaño de lo escrito.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private string? _hex;

        public long BytesWritten { get; private set; }

        public string GetHashHex() => _hex ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            BytesWritten += buffer.Length;
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            BytesWritten += buffer.Length;
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GetHashHex();
                _hash.Dispose();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
