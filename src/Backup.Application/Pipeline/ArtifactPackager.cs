using System.Security.Cryptography;
using Backup.Application.Providers;
using Backup.Domain.Jobs;

namespace Backup.Application.Pipeline;

/// <param name="RawSize">Tamaño del contenido antes de comprimir y cifrar.</param>
public sealed record PackagedArtifact(string FilePath, string Extensions, long Size, string Sha256, long RawSize);

/// <summary>Aplica las transformaciones registradas (compresión, cifrado...) al artefacto de origen.</summary>
public sealed class ArtifactPackager(IEnumerable<IStreamTransform> transforms)
{
    private readonly IReadOnlyList<IStreamTransform> _transforms = [.. transforms.OrderBy(t => t.Order)];

    /// <summary>Empaqueta un artefacto ya generado en disco y, al terminar, lo borra (ya no hace falta).</summary>
    public async Task<PackagedArtifact> PackageAsync(
        BackupJob job, BackupArtifact artifact, IRunLog log, CancellationToken cancellationToken)
    {
        var packaged = await PackageAsync(job, artifact.Extension, artifact.FilePath + ".pkg", async pipeline =>
        {
            await using var input = new FileStream(artifact.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await input.CopyToAsync(pipeline, cancellationToken);
        }, log, cancellationToken);

        TryDelete(artifact.FilePath);
        return packaged;
    }

    /// <summary>
    /// Empaqueta lo que <paramref name="write"/> escriba en el stream que recibe: pasa por las transformaciones y se
    /// guarda en <paramref name="outputPath"/> calculando el SHA-256, sin archivo intermedio.
    /// </summary>
    public async Task<PackagedArtifact> PackageAsync(
        BackupJob job, string extension, string outputPath, Func<Stream, Task> write, IRunLog log, CancellationToken cancellationToken)
    {
        var active = _transforms.Where(t => t.AppliesTo(job)).ToList();
        var extensions = extension + string.Concat(active.Select(t => t.GetExtension(job)));

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

            long rawSize;
            await using (var counting = new CountingStream(pipeline))
            {
                await write(counting);
                rawSize = counting.BytesWritten;
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

            return new PackagedArtifact(outputPath, extensions, hashing.BytesWritten, hashing.GetHashHex(), rawSize);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Se borra con la carpeta de trabajo al final.
        }
    }

    /// <summary>Cuenta lo escrito y lo pasa al pipeline; al cerrarse no cierra el pipeline (lo cierra el empaquetador).</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BytesWritten += buffer.Length;
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
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
