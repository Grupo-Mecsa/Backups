using System.Security.Cryptography;
using System.Text;

namespace Backup.Infrastructure.Transforms;

/// <summary>
/// Formato de cifrado de respaldos (encrypt-then-MAC, apto para archivos grandes en streaming):
/// <code>
/// "BKE1" | salt(16) | iv(16) | AES-256-CBC(PKCS7)(datos) | HMAC-SHA256(32) sobre todo lo anterior
/// </code>
/// Las llaves de cifrado y MAC se derivan de la contraseña con PBKDF2-SHA256.
/// </summary>
public static class BackupEncryption
{
    public const string FileExtension = ".enc";

    private static readonly byte[] Magic = "BKE1"u8.ToArray();
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int MacSize = 32;
    private const int Iterations = 210_000;
    private const int HeaderSize = 4 + SaltSize + IvSize;

    public static Stream CreateEncryptingStream(Stream output, string passphrase) =>
        new EncryptingStream(output, passphrase);

    /// <summary>Verifica la integridad y descifra un respaldo. Requiere un stream con posicionamiento.</summary>
    /// <exception cref="CryptographicException">Contraseña incorrecta o archivo alterado.</exception>
    public static async Task DecryptAsync(Stream input, Stream output, string passphrase, CancellationToken cancellationToken = default)
    {
        if (!input.CanSeek)
        {
            throw new ArgumentException("El stream de entrada debe permitir posicionamiento.", nameof(input));
        }

        if (input.Length < HeaderSize + MacSize)
        {
            throw new CryptographicException("El archivo es demasiado corto para ser un respaldo cifrado.");
        }

        var header = new byte[HeaderSize];
        input.Position = 0;
        await input.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("El archivo no tiene el formato de cifrado esperado.");
        }

        var salt = header.AsSpan(4, SaltSize).ToArray();
        var iv = header.AsSpan(4 + SaltSize, IvSize).ToArray();
        var (encKey, macKey) = DeriveKeys(passphrase, salt);

        var payloadEnd = input.Length - MacSize;

        // 1) Verificar el MAC antes de descifrar nada.
        using (var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey))
        {
            input.Position = 0;
            await CopyRangeAsync(input, payloadEnd, chunk => hmac.AppendData(chunk.Span), cancellationToken);
            var expected = new byte[MacSize];
            input.Position = payloadEnd;
            await input.ReadExactlyAsync(expected, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(hmac.GetHashAndReset(), expected))
            {
                throw new CryptographicException("Contraseña incorrecta o archivo alterado.");
            }
        }

        // 2) Descifrar.
        using var aes = Aes.Create();
        aes.Key = encKey;
        using var decryptor = aes.CreateDecryptor(encKey, iv);
        await using var crypto = new CryptoStream(output, decryptor, CryptoStreamMode.Write, leaveOpen: true);
        input.Position = HeaderSize;
        var buffer = new byte[1 << 16];
        var remaining = payloadEnd - HeaderSize;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            await crypto.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        await crypto.FlushFinalBlockAsync(cancellationToken);
    }

    private static async Task CopyRangeAsync(Stream input, long end, Action<ReadOnlyMemory<byte>> sink, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        while (input.Position < end)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - input.Position)), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            sink(buffer.AsMemory(0, read));
        }
    }

    private static (byte[] EncKey, byte[] MacKey) DeriveKeys(string passphrase, byte[] salt)
    {
        var material = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, 64);
        return (material[..32], material[32..]);
    }

    /// <summary>Stream de escritura: cifra con AES-CBC y agrega el HMAC al cerrarse.</summary>
    private sealed class EncryptingStream : Stream
    {
        private readonly Stream _output;
        private readonly IncrementalHash _hmac;
        private readonly Aes _aes;
        private readonly ICryptoTransform _encryptor;
        private readonly CryptoStream _crypto;
        private bool _closed;

        public EncryptingStream(Stream output, string passphrase)
        {
            _output = output;
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var iv = RandomNumberGenerator.GetBytes(IvSize);
            var (encKey, macKey) = DeriveKeys(passphrase, salt);

            _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
            _aes = Aes.Create();
            _encryptor = _aes.CreateEncryptor(encKey, iv);

            var tee = new MacTeeStream(output, _hmac);
            tee.Write(Magic);
            tee.Write(salt);
            tee.Write(iv);
            _crypto = new CryptoStream(tee, _encryptor, CryptoStreamMode.Write, leaveOpen: true);
        }

        public override void Write(byte[] buffer, int offset, int count) => _crypto.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _crypto.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _crypto.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _crypto.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() { }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_closed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                _crypto.FlushFinalBlock();
                _crypto.Dispose();
                _output.Write(_hmac.GetHashAndReset());
                _hmac.Dispose();
                _encryptor.Dispose();
                _aes.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Escribe en el destino y alimenta el HMAC con los mismos bytes.</summary>
    private sealed class MacTeeStream(Stream inner, IncrementalHash hmac) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hmac.AppendData(buffer);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hmac.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
